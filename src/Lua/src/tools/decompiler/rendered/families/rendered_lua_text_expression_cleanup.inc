struct RenderedLuaFunctionTemporaryUsage {
    std::string temporary_name;
    std::size_t line_index = 0;
    std::size_t function_end_line = 0;
    std::size_t use_line = 0;
    std::size_t use_count = 0;
};

bool CollectRenderedLuaFunctionTemporaryUsage(const std::vector<std::string>& lines,
                                              std::size_t line_index,
                                              RenderedLuaFunctionTemporaryUsage* usage_out) {
    if (line_index >= lines.size() || usage_out == nullptr) {
        return false;
    }

    std::string temporary_name;
    std::string temporary_expression;
    if (!IsTemporaryAssignmentLine(
            lines[line_index], &temporary_name, &temporary_expression)) {
        return false;
    }
    if (temporary_expression.rfind("function(", 0) != 0) {
        return false;
    }

    const std::size_t function_end_line = FindRenderedFunctionBlockEnd(lines, line_index);
    if (function_end_line >= lines.size() || function_end_line <= line_index) {
        return false;
    }

    std::size_t use_line = lines.size();
    std::size_t use_count = 0;
    const std::size_t scope_end = FindEnclosingRenderedFunctionBlockEnd(lines, line_index);
    const std::size_t probe_limit =
        scope_end < lines.size() ? scope_end + 1 : lines.size();
    for (std::size_t probe = function_end_line + 1; probe < probe_limit; ++probe) {
        if (IsRenderedShadowingLocalDeclaration(lines[probe], temporary_name)) {
            break;
        }

        if (IsAssignmentToTemporary(lines[probe], temporary_name)) {
            break;
        }

        const std::size_t line_uses = CountIdentifierUses(lines[probe], temporary_name);
        if (line_uses == 0) {
            continue;
        }
        use_count += line_uses;
        use_line = probe;
        if (use_count > 1) {
            break;
        }
    }

    usage_out->temporary_name = std::move(temporary_name);
    usage_out->line_index = line_index;
    usage_out->function_end_line = function_end_line;
    usage_out->use_line = use_line;
    usage_out->use_count = use_count;
    return true;
}

bool SimplifyRenderedLuaFunctionTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr std::string_view kStepName = "SimplifyRenderedLuaFunctionTemporaries";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            RenderedLuaFunctionTemporaryUsage usage;
            if (!CollectRenderedLuaFunctionTemporaryUsage(*lines, line_index, &usage)) {
                continue;
            }

            if (usage.use_count == 0) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(usage.line_index),
                             lines->begin() + static_cast<std::ptrdiff_t>(usage.function_end_line) + 1);
                changed = true;
                pass_changed = true;
                break;
            }

            if (usage.use_count != 1 || usage.use_line >= lines->size()) {
                continue;
            }

            const std::size_t found = FindIdentifierUse(
                (*lines)[usage.use_line], usage.temporary_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::size_t equal = (*lines)[usage.use_line].find('=');
            if (equal != std::string::npos && found <= equal) {
                continue;
            }

            const std::size_t function_begin = (*lines)[usage.line_index].find("function(");
            if (function_begin == std::string::npos) {
                continue;
            }

            const std::string use_prefix = (*lines)[usage.use_line].substr(0, found);
            const std::string use_suffix = (*lines)[usage.use_line].substr(
                found + usage.temporary_name.size());
            const std::string_view trimmed_suffix = TrimLuaWhitespace(use_suffix);
            const bool requires_call_wrap =
                !trimmed_suffix.empty() &&
                (trimmed_suffix.front() == '(' || trimmed_suffix.front() == '[' ||
                 trimmed_suffix.front() == '.' || trimmed_suffix.front() == ':');

            std::vector<std::string> rebuilt;
            rebuilt.reserve(lines->size() + (usage.function_end_line - usage.line_index));
            for (std::size_t source = 0; source < lines->size(); ++source) {
                if (source == usage.line_index) {
                    source = usage.function_end_line;
                    continue;
                }

                if (source == usage.use_line) {
                    rebuilt.push_back(use_prefix +
                                      (requires_call_wrap ? "(" : std::string()) +
                                      (*lines)[usage.line_index].substr(function_begin));
                    for (std::size_t body = usage.line_index + 1;
                         body <= usage.function_end_line;
                         ++body) {
                        std::string rebuilt_body_line = (*lines)[body];
                        if (body == usage.function_end_line) {
                            if (requires_call_wrap) {
                                rebuilt_body_line += ")";
                            }
                            rebuilt_body_line += use_suffix;
                        }
                        rebuilt.push_back(std::move(rebuilt_body_line));
                    }
                    continue;
                }

                rebuilt.push_back((*lines)[source]);
            }

            *lines = std::move(rebuilt);
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaAssignedFunctionTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr std::string_view kStepName = "SimplifyRenderedLuaAssignedFunctionTemporaries";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            RenderedLuaFunctionTemporaryUsage usage;
            if (!CollectRenderedLuaFunctionTemporaryUsage(*lines, line_index, &usage)) {
                continue;
            }

            if (usage.use_count != 1 || usage.use_line >= lines->size()) {
                continue;
            }

            const std::string_view use_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[usage.use_line]));
            const std::size_t equal = use_trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            const std::string_view rhs = TrimLuaWhitespace(use_trimmed.substr(equal + 1));
            if (rhs != usage.temporary_name) {
                continue;
            }

            const std::size_t found = FindIdentifierUse(
                (*lines)[usage.use_line], usage.temporary_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::size_t function_begin = (*lines)[usage.line_index].find("function(");
            if (function_begin == std::string::npos) {
                continue;
            }

            std::vector<std::string> rebuilt;
            rebuilt.reserve(lines->size() + (usage.function_end_line - usage.line_index));
            for (std::size_t source = 0; source < lines->size(); ++source) {
                if (source == usage.line_index) {
                    source = usage.function_end_line;
                    continue;
                }

                if (source == usage.use_line) {
                    rebuilt.push_back((*lines)[usage.use_line].substr(0, found) +
                                      (*lines)[usage.line_index].substr(function_begin));
                    for (std::size_t body = usage.line_index + 1;
                         body <= usage.function_end_line;
                         ++body) {
                        rebuilt.push_back((*lines)[body]);
                    }
                    continue;
                }

                rebuilt.push_back((*lines)[source]);
            }

            *lines = std::move(rebuilt);
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaConditionalIifes(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 6 < lines->size(); ++line_index) {
        const std::string& start_line = (*lines)[line_index];
        const std::string_view start_trimmed =
            TrimLuaWhitespace(StripLuaLineComment(start_line));
        const std::size_t function_pos_trimmed = start_trimmed.find("(function()");
        if (function_pos_trimmed == std::string_view::npos) {
            continue;
        }
        const std::size_t function_pos = start_line.find("(function()");
        if (function_pos == std::string::npos) {
            continue;
        }

        const std::string_view if_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        const std::string_view then_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
        const std::string_view else_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
        const std::string_view else_return_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4]));
        const std::string_view end_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 5]));
        const std::string_view call_end_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 6]));

        if (!StartsWithLuaKeyword(if_trimmed, "if") || if_trimmed.size() < 8 ||
            !if_trimmed.ends_with("then") || !StartsWithLuaKeyword(then_trimmed, "return") ||
            else_trimmed != "else" || !StartsWithLuaKeyword(else_return_trimmed, "return") ||
            end_trimmed != "end" || call_end_trimmed != "end)(),") {
            continue;
        }

        const std::string_view condition =
            TrimLuaWhitespace(if_trimmed.substr(2, if_trimmed.size() - 2 - 4));
        const std::string_view then_expression = TrimLuaWhitespace(then_trimmed.substr(6));
        const std::string_view else_expression =
            TrimLuaWhitespace(else_return_trimmed.substr(6));
        if (condition.empty() || then_expression.empty() || else_expression.empty() ||
            !IsRenderedDefinitelyTruthyExpression(then_expression)) {
            continue;
        }

        const std::string prefix = start_line.substr(0, function_pos);
        const std::string combined =
            "((" + std::string(condition) + ") and (" + std::string(then_expression) +
            ") or (" + std::string(else_expression) + "))";
        (*lines)[line_index] = prefix + combined + ",";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 7));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaAssertConditionBlocks(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr std::string_view kStepName = "SimplifyRenderedLuaAssertConditionBlocks";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
            const std::string_view trimmed_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
                continue;
            }

            const std::string_view trimmed_then =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
            const std::string_view trimmed_else =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
            const std::string_view trimmed_else_body =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
            const std::string_view trimmed_end =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4]));

            if (trimmed_else != "else" || trimmed_end != "end") {
                continue;
            }

            bool preserve_condition = false;
            if (trimmed_then == "assert(true)" && trimmed_else_body == "assert(false)") {
                preserve_condition = true;
            } else if (!(trimmed_then == "assert(false)" &&
                         trimmed_else_body == "assert(true)")) {
                continue;
            }

            const std::string_view condition =
                trimmed_if.substr(3, trimmed_if.size() - 3 - 5);
            if (TrimLuaWhitespace(condition).empty()) {
                continue;
            }

            const std::size_t first_non_space =
                (*lines)[line_index].find_first_not_of(" \t");
            const std::string indent =
                first_non_space == std::string::npos
                    ? std::string()
                    : (*lines)[line_index].substr(0, first_non_space);

            std::string assertion_expression;
            if (preserve_condition) {
                assertion_expression = std::string(condition);
            } else {
                assertion_expression = "not " + WrapInlineExpression(condition);
            }

            (*lines)[line_index] = indent + "assert(" + assertion_expression + ")";
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                         lines->begin() + static_cast<std::ptrdiff_t>(line_index + 5));
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaConditionHeaders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view stripped = StripLuaLineComment(line);
        if (stripped.size() != line.size()) {
            continue;
        }

        const std::size_t first_non_space = line.find_first_not_of(" \t");
        const std::string indent =
            first_non_space == std::string::npos ? std::string() : line.substr(0, first_non_space);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);

        std::string_view keyword;
        std::string_view suffix;
        std::string_view condition;
        if (trimmed.starts_with("if ") && trimmed.size() > 7 && trimmed.ends_with(" then")) {
            keyword = "if ";
            suffix = " then";
            condition = trimmed.substr(3, trimmed.size() - 3 - 5);
        } else if (trimmed.starts_with("elseif ") && trimmed.size() > 11 &&
                   trimmed.ends_with(" then")) {
            keyword = "elseif ";
            suffix = " then";
            condition = trimmed.substr(7, trimmed.size() - 7 - 5);
        } else if (trimmed.starts_with("while ") && trimmed.size() > 9 &&
                   trimmed.ends_with(" do")) {
            keyword = "while ";
            suffix = " do";
            condition = trimmed.substr(6, trimmed.size() - 6 - 3);
        } else if (trimmed.starts_with("until ") && trimmed.size() > 6) {
            keyword = "until ";
            suffix = "";
            condition = trimmed.substr(6);
        } else {
            continue;
        }

        bool local_changed = false;
        const std::string normalized =
            NormalizeRenderedConditionExpression(condition, &local_changed);
        if (!local_changed) {
            continue;
        }

        line = indent + std::string(keyword) + normalized + std::string(suffix);
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaExpressionParentheses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        std::string rewritten;
        rewritten.reserve(line.size());
        bool line_changed = false;
        for (std::size_t cursor = 0; cursor < line.size();) {
            if (line[cursor] != '(') {
                rewritten.push_back(line[cursor]);
                ++cursor;
                continue;
            }

            std::size_t outer_close = std::string::npos;
            if (!FindRenderedMatchingParen(line, cursor, &outer_close) ||
                outer_close <= cursor + 1) {
                rewritten.push_back(line[cursor]);
                ++cursor;
                continue;
            }

            const auto skip_spaces = [&](std::size_t index) {
                while (index < outer_close &&
                       std::isspace(static_cast<unsigned char>(line[index])) != 0) {
                    ++index;
                }
                return index;
            };
            const auto match_keyword = [&](std::size_t index,
                                           std::string_view keyword,
                                           std::size_t* after_index) {
                index = skip_spaces(index);
                if (index + keyword.size() > outer_close ||
                    std::string_view(line).substr(index, keyword.size()) != keyword) {
                    return false;
                }
                const std::size_t end = index + keyword.size();
                if (end < outer_close &&
                    std::isalnum(static_cast<unsigned char>(line[end])) != 0) {
                    return false;
                }
                if (after_index != nullptr) {
                    *after_index = end;
                }
                return true;
            };

            std::size_t first_open = skip_spaces(cursor + 1);
            if (first_open >= outer_close || line[first_open] != '(') {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            std::size_t first_close = std::string::npos;
            std::size_t second_open = std::string::npos;
            std::size_t second_close = std::string::npos;
            std::size_t third_open = std::string::npos;
            std::size_t third_close = std::string::npos;
            if (!FindRenderedMatchingParen(line, first_open, &first_close) ||
                first_close >= outer_close ||
                !match_keyword(first_close + 1, "and", &second_open)) {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            second_open = skip_spaces(second_open);
            if (second_open >= outer_close || line[second_open] != '(' ||
                !FindRenderedMatchingParen(line, second_open, &second_close) ||
                second_close >= outer_close ||
                !match_keyword(second_close + 1, "or", &third_open)) {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            third_open = skip_spaces(third_open);
            if (third_open >= outer_close || line[third_open] != '(' ||
                !FindRenderedMatchingParen(line, third_open, &third_close) ||
                third_close != outer_close - 1) {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            const std::string_view condition = TrimLuaWhitespace(
                std::string_view(line).substr(first_open + 1, first_close - first_open - 1));
            const std::string_view then_expression = TrimLuaWhitespace(
                std::string_view(line).substr(second_open + 1, second_close - second_open - 1));
            const std::string_view else_expression = TrimLuaWhitespace(
                std::string_view(line).substr(third_open + 1, third_close - third_open - 1));
            if (condition.empty() || then_expression.empty() || else_expression.empty()) {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            rewritten += "(";
            rewritten += std::string(condition);
            rewritten += " and ";
            rewritten += std::string(then_expression);
            rewritten += " or ";
            rewritten += std::string(else_expression);
            rewritten += ")";
            cursor = outer_close + 1;
            line_changed = true;
        }

        if (line_changed) {
            line = std::move(rewritten);
            changed = true;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaEscapingGuardedLocalDeclarations(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    auto build_positive_expression = [](std::string_view condition,
                                        std::string* expression_out) -> bool {
        if (expression_out == nullptr) {
            return false;
        }

        if (TryBuildRenderedPositiveFallbackExpression(condition, expression_out)) {
            return true;
        }

        std::vector<std::string> and_parts;
        if (!SplitRenderedTopLevelBooleanChain(condition, "and", &and_parts) ||
            and_parts.size() < 2) {
            return false;
        }

        std::string rebuilt;
        for (std::size_t part_index = 0; part_index < and_parts.size(); ++part_index) {
            std::string_view part = TrimLuaWhitespace(and_parts[part_index]);
            std::string positive;
            if (part_index + 1 == and_parts.size()) {
                if (!StartsWithLuaKeyword(part, "not")) {
                    return false;
                }
                positive = NormalizeRenderedConditionOperand(
                    TrimLuaWhitespace(part.substr(3)),
                    nullptr);
            } else {
                if (StartsWithLuaKeyword(part, "not")) {
                    return false;
                }
                positive = NormalizeRenderedConditionOperand(part, nullptr);
            }

            if (positive.empty()) {
                return false;
            }

            if (rebuilt.empty()) {
                rebuilt = positive;
            } else {
                rebuilt = RenderLogicalExpression(rebuilt, "and", positive);
            }
        }

        if (rebuilt.empty()) {
            return false;
        }
        *expression_out = rebuilt;
        return true;
    };

    bool changed = false;
    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        std::string local_name;
        std::string fallback_rhs = "nil";
        bool line_is_local_assignment = false;

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                              &assign_is_local,
                                              &assign_lhs,
                                              &assign_rhs) &&
            assign_is_local && IsValidLuaIdentifier(assign_lhs)) {
            local_name = assign_lhs;
            fallback_rhs = std::string(TrimLuaWhitespace(assign_rhs));
            line_is_local_assignment = true;
        } else {
            std::vector<std::string> local_names;
            if (!ParseRenderedLocalDeclarationLine((*lines)[line_index + 1], &local_names) ||
                local_names.size() != 1 || !IsValidLuaIdentifier(local_names.front())) {
                continue;
            }
            local_name = local_names.front();
        }

        std::string positive_expression;
        if (!build_positive_expression(condition, &positive_expression) ||
            positive_expression.empty() ||
            CountIdentifierUses(positive_expression, local_name) != 0 ||
            CountIdentifierUses(fallback_rhs, local_name) != 0 ||
            !ShouldEmitLocalForRenderedIdentifierTarget(*lines, line_index + 1, local_name)) {
            continue;
        }

        const std::size_t function_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index + 1);
        if (function_end >= lines->size()) {
            continue;
        }

        const std::vector<std::size_t>& declaration_scope = scope_paths[line_index + 1];
        bool escapes_scope = false;
        for (std::size_t future = line_index + 3; future < function_end; ++future) {
            if (IsRenderedFunctionStartLine((*lines)[future])) {
                const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, future);
                if (nested_end >= lines->size() || nested_end <= future) {
                    break;
                }
                future = nested_end;
                continue;
            }

            if (CountIdentifierUses((*lines)[future], local_name) == 0) {
                continue;
            }

            if (!IsRenderedScopePathPrefix(declaration_scope, scope_paths[future])) {
                escapes_scope = true;
                break;
            }
        }
        if (!escapes_scope) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + "local " + local_name + " = " +
            RenderLogicalExpression(positive_expression, "or", fallback_rhs);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));

        if (line_index + 1 < lines->size()) {
            std::vector<std::string> trailing_local_names;
            bool trailing_is_local = false;
            std::string trailing_lhs;
            std::string trailing_rhs;
            if (ParseRenderedLocalDeclarationLine((*lines)[line_index + 1], &trailing_local_names) &&
                trailing_local_names.size() == 1 && trailing_local_names.front() == local_name) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
            } else if (ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                                         &trailing_is_local,
                                                         &trailing_lhs,
                                                         &trailing_rhs) &&
                       trailing_is_local && trailing_lhs == local_name &&
                       TrimLuaWhitespace(trailing_rhs) == "nil" && !line_is_local_assignment) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
            }
        }

        ComputeRenderedScopePaths(*lines, &scope_paths);
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaClosureCarryShadowAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto next_nonblank = [&](std::size_t start) {
        while (start < lines->size() &&
               TrimLuaWhitespace(StripLuaLineComment((*lines)[start])).empty()) {
            ++start;
        }
        return start;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 9 < lines->size(); ++line_index) {
        bool outer_is_local = false;
        std::vector<std::string> outer_lhs_names;
        std::string outer_rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &outer_is_local,
                                         &outer_lhs_names,
                                         &outer_rhs_expression) ||
            !outer_is_local || outer_lhs_names.size() != 2 ||
            !IsValidLuaIdentifier(outer_lhs_names[0]) ||
            !IsValidLuaIdentifier(outer_lhs_names[1])) {
            continue;
        }

        const std::size_t closure_line = next_nonblank(line_index + 1);
        if (closure_line >= lines->size() ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[closure_line]))
                    .rfind("return function(", 0) != 0) {
            continue;
        }

        const std::size_t closure_end = FindRenderedFunctionBlockEnd(*lines, closure_line);
        if (closure_end >= lines->size() || closure_end <= closure_line) {
            continue;
        }

        std::vector<std::size_t> body_lines;
        for (std::size_t probe = closure_line + 1; probe < closure_end; ++probe) {
            if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                body_lines.push_back(probe);
            }
        }
        if (body_lines.size() != 9) {
            continue;
        }

        bool inner_is_local = false;
        std::vector<std::string> inner_lhs_names;
        std::string inner_rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[body_lines[0]],
                                         &inner_is_local,
                                         &inner_lhs_names,
                                         &inner_rhs_expression) ||
            !inner_is_local || inner_lhs_names.size() != 2 ||
            !IsValidLuaIdentifier(inner_lhs_names[0]) ||
            !IsValidLuaIdentifier(inner_lhs_names[1])) {
            continue;
        }

        const std::string outer_value = outer_lhs_names[0];
        const std::string outer_flag = outer_lhs_names[1];
        const std::string inner_value = inner_lhs_names[0];
        const std::string inner_flag = inner_lhs_names[1];

        if (TrimLuaWhitespace(StripLuaLineComment((*lines)[body_lines[1]])) !=
                ("if " + outer_flag + " then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[body_lines[2]])) !=
                ("if " + inner_flag + " then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[body_lines[4]])) != "else" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[body_lines[6]])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[body_lines[7]])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[body_lines[5]])) !=
                ("return " + outer_value) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[body_lines[8]])) !=
                ("return " + outer_value)) {
            continue;
        }

        bool shadow_is_local = false;
        std::string shadow_lhs;
        std::string shadow_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[body_lines[3]],
                                               &shadow_is_local,
                                               &shadow_lhs,
                                               &shadow_rhs) ||
            !shadow_is_local || shadow_lhs != outer_value ||
            NormalizeRenderedConditionOperand(TrimLuaWhitespace(shadow_rhs), nullptr) !=
                inner_value) {
            continue;
        }

        const std::string update_indent(GetRenderedLuaIndent((*lines)[body_lines[3]]));
        const std::string else_indent(GetRenderedLuaIndent((*lines)[body_lines[5]]));
        const std::string return_indent(GetRenderedLuaIndent((*lines)[body_lines[8]]));
        (*lines)[body_lines[3]] = update_indent + outer_value + " = " + inner_value;
        (*lines)[body_lines[5]] = else_indent + inner_value + " = " + outer_value;
        (*lines)[body_lines[8]] = return_indent + "return " + inner_value;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedNilFallbackReturns(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !assign_is_local || !IsValidLuaIdentifier(assign_lhs)) {
            continue;
        }

        assign_rhs = StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        std::vector<std::string> rhs_or_parts;
        if (!SplitRenderedTopLevelBooleanChain(assign_rhs, "or", &rhs_or_parts) ||
            rhs_or_parts.size() != 2 ||
            NormalizeRenderedConditionOperand(TrimLuaWhitespace(rhs_or_parts.back()), nullptr) !=
                "nil") {
            continue;
        }

        const std::string_view if_line =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (if_line != ("if " + assign_lhs + " then")) {
            continue;
        }

        const std::size_t block_end = FindRenderedFunctionBlockEnd(*lines, line_index + 1);
        if (block_end >= lines->size() || block_end + 1 >= lines->size()) {
            continue;
        }

        const std::string_view fallback_return =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[block_end + 1]));
        if (fallback_return != ("return " + assign_lhs + ", false")) {
            continue;
        }

        bool has_guarded_return = false;
        for (std::size_t probe = line_index + 2; probe < block_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (StartsWithLuaKeyword(trimmed, "return")) {
                has_guarded_return = true;
                break;
            }
        }
        if (!has_guarded_return) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[block_end + 1]));
        (*lines)[block_end + 1] = indent + "return nil, false";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaDotAccess(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        std::string rewritten;
        rewritten.reserve(line.size());
        for (std::size_t i = 0; i < line.size();) {
            if (i + 4 < line.size() && line[i] == '[' && line[i + 1] == '"') {
                std::size_t end_quote = line.find("\"]", i + 2);
                if (end_quote != std::string::npos) {
                    const std::string_view key(line.data() + i + 2, end_quote - (i + 2));
                    if (IsValidLuaIdentifier(key)) {
                        rewritten.push_back('.');
                        rewritten.append(key);
                        i = end_quote + 2;
                        changed = true;
                        continue;
                    }
                }
            }
            rewritten.push_back(line[i]);
            ++i;
        }
        line = std::move(rewritten);
    }
    return changed;
}

bool SimplifyRenderedLuaCallParentheses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        std::size_t start = 0;
        while (true) {
            const std::size_t open = line.find('(', start);
            if (open == std::string::npos || open + 2 >= line.size()) {
                break;
            }

            const std::size_t close = line.find(")(", open);
            if (close == std::string::npos) {
                break;
            }

            const std::string_view candidate(line.data() + open + 1,
                                             close - (open + 1));
            const std::string_view trimmed = TrimLuaWhitespace(candidate);
            const bool simple_dotted_chain =
                !trimmed.empty() &&
                std::all_of(trimmed.begin(), trimmed.end(), [](char ch) {
                    const unsigned char uch = static_cast<unsigned char>(ch);
                    return std::isalnum(uch) != 0 || ch == '_' || ch == '.';
                });
            if (!simple_dotted_chain) {
                start = open + 1;
                continue;
            }

            line.erase(close, 1);
            line.erase(open, 1);
            changed = true;
            start = open + trimmed.size();
        }
    }
    return changed;
}
