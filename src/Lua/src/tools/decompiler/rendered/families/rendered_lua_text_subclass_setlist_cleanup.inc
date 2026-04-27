bool SimplifyRenderedLuaSubclassBootstrapSetlistChains(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto is_simple_seed_expression = [](std::string_view expression) {
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        if (normalized.empty()) {
            return false;
        }
        return normalized == "true" || normalized == "false" || normalized == "nil" ||
               LooksLikeRenderedNumericLiteral(normalized) ||
               normalized.front() == '"' || normalized.front() == '\'' ||
               (!IsRenderTemporaryName(normalized) &&
                IsRenderedSafePrefixExpression(normalized));
    };

    const auto parse_temp_initializer_call =
        [&](std::string_view line,
            std::string_view callee_name,
            std::string_view temp_name,
            std::string* literal_out) {
            const std::string normalized =
                StripRenderedWholeParentheses(TrimLuaWhitespace(StripLuaLineComment(line)),
                                              nullptr);
            const std::string expected_prefix = std::string(callee_name) + "(";
            if (!normalized.starts_with(expected_prefix) || normalized.back() != ')') {
                return false;
            }

            std::vector<std::string> arguments;
            if (!SplitRenderedTopLevelCommaChain(
                    TrimLuaWhitespace(normalized.substr(expected_prefix.size(),
                                                        normalized.size() -
                                                            expected_prefix.size() - 1)),
                    &arguments) ||
                arguments.size() < 2) {
                return false;
            }

            const std::string normalized_first =
                StripRenderedWholeParentheses(TrimLuaWhitespace(arguments.front()), nullptr);
            if (normalized_first != temp_name) {
                return false;
            }

            for (std::size_t index = 1; index < arguments.size(); ++index) {
                if (!is_simple_seed_expression(arguments[index])) {
                    return false;
                }
            }

            if (literal_out != nullptr) {
                std::string literal = "{";
                for (std::size_t index = 1; index < arguments.size(); ++index) {
                    if (index != 1) {
                        literal += ", ";
                    }
                    literal += std::string(TrimLuaWhitespace(arguments[index]));
                }
                literal += "}";
                *literal_out = std::move(literal);
            }
            return true;
        };

    return RunRenderedLuaCleanupPassLoop(__func__, 16, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            bool alias_is_local = false;
            std::string alias_lhs;
            std::string alias_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &alias_is_local,
                                                   &alias_lhs,
                                                   &alias_rhs) ||
                !alias_is_local) {
                continue;
            }

            const std::string class_alias =
                StripRenderedWholeParentheses(TrimLuaWhitespace(alias_lhs), nullptr);
            const std::string class_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(alias_rhs), nullptr);
            if (!IsRenderTemporaryName(class_alias) || !IsValidLuaIdentifier(class_name)) {
                continue;
            }

            std::size_t subclass_initializer_line = lines->size();
            std::string subclass_initializer_indent;
            std::string subclass_base_expression;
            for (std::size_t probe = line_index; probe-- > 0;) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    continue;
                }

                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }

                std::string parsed_base_expression;
                std::string parsed_subclass_name;
                if (ParseRenderedCreateSubclassStatement((*lines)[probe],
                                                        &parsed_base_expression,
                                                        &parsed_subclass_name) &&
                    parsed_subclass_name == class_name) {
                    subclass_initializer_line = probe;
                    subclass_initializer_indent =
                        std::string(GetRenderedLuaIndent((*lines)[probe]));
                    subclass_base_expression = std::move(parsed_base_expression);
                }
                break;
            }

            bool modes_is_local = false;
            std::string modes_lhs;
            std::string modes_rhs;
            if (line_index + 1 >= scope_paths.size() || !scope_paths[line_index + 1].empty() ||
                !ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                                   &modes_is_local,
                                                   &modes_lhs,
                                                   &modes_rhs) ||
                !modes_is_local) {
                continue;
            }

            const std::string modes_temp =
                StripRenderedWholeParentheses(TrimLuaWhitespace(modes_lhs), nullptr);
            const std::string normalized_modes_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(modes_rhs), nullptr);
            if (!IsRenderTemporaryName(modes_temp) || normalized_modes_rhs != "{}") {
                continue;
            }

            std::size_t banner_begin = line_index + 2;
            std::size_t call_line = lines->size();
            std::size_t assignment_line = lines->size();
            std::string modes_literal;
            for (std::size_t probe = line_index + 2; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    break;
                }

                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty() || IsRenderedSetlistErrorBanner((*lines)[probe])) {
                    continue;
                }

                if (call_line == lines->size() &&
                    parse_temp_initializer_call((*lines)[probe],
                                               class_alias,
                                               modes_temp,
                                               &modes_literal)) {
                    call_line = probe;
                    continue;
                }

                assignment_line = probe;
                break;
            }

            if (assignment_line >= lines->size() || call_line >= lines->size() ||
                modes_literal.empty()) {
                continue;
            }

            const std::string_view assignment_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[assignment_line]));
            const std::size_t equal = assignment_trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            std::vector<std::string> lhs_parts;
            std::vector<std::string> rhs_parts;
            if (!SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(assignment_trimmed.substr(0, equal)),
                                                 &lhs_parts) ||
                lhs_parts.size() < 3) {
                continue;
            }

            const std::string_view rhs_view =
                TrimLuaWhitespace(assignment_trimmed.substr(equal + 1));
            if (!SplitRenderedTopLevelCommaChain(rhs_view, &rhs_parts)) {
                const std::size_t function_pos = rhs_view.find("function(");
                if (function_pos == std::string_view::npos) {
                    continue;
                }

                std::string_view prefix = TrimLuaWhitespace(rhs_view.substr(0, function_pos));
                if (!prefix.empty() && prefix.back() == ',') {
                    prefix = TrimLuaWhitespace(prefix.substr(0, prefix.size() - 1));
                }
                if (!prefix.empty() &&
                    !SplitRenderedTopLevelCommaChain(prefix, &rhs_parts)) {
                    continue;
                }
                rhs_parts.push_back(std::string(TrimLuaWhitespace(rhs_view.substr(function_pos))));
            }

            std::size_t function_temp_index = lhs_parts.size();
            std::string function_temp_name;
            for (std::size_t index = 0; index < lhs_parts.size() && index < rhs_parts.size(); ++index) {
                const std::string normalized_lhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[index]), nullptr);
                const std::string normalized_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[index]), nullptr);
                if (!IsRenderTemporaryName(normalized_lhs) ||
                    !normalized_rhs.starts_with("function(")) {
                    continue;
                }
                function_temp_index = index;
                function_temp_name = normalized_lhs;
                break;
            }
            if (function_temp_index == lhs_parts.size()) {
                continue;
            }

            std::string method_target;
            std::vector<std::pair<std::string, std::string>> preserved_field_seeds;
            const std::string method_prefix = class_alias + ".";
            const std::string class_method_prefix = class_name + ".";
            std::string maintained_modes_target = class_alias + ".MaintainedModes";
            for (std::size_t index = 0; index < function_temp_index; ++index) {
                const std::string normalized_lhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[index]), nullptr);
                const std::string normalized_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[index]), nullptr);
                const bool uses_alias_prefix = normalized_lhs.starts_with(method_prefix);
                const bool uses_class_prefix = normalized_lhs.starts_with(class_method_prefix);
                if (!uses_alias_prefix && !uses_class_prefix) {
                    continue;
                }
                if (normalized_lhs.ends_with(".MaintainedModes")) {
                    maintained_modes_target = normalized_lhs;
                    continue;
                }
                if (normalized_rhs == function_temp_name) {
                    method_target = normalized_lhs;
                    continue;
                }
                if (is_simple_seed_expression(normalized_rhs)) {
                    preserved_field_seeds.emplace_back(normalized_lhs,
                                                       std::string(TrimLuaWhitespace(rhs_parts[index])));
                }
            }

            if (method_target.empty()) {
                continue;
            }

            const std::string assignment_indent(GetRenderedLuaIndent((*lines)[assignment_line]));
            if (subclass_initializer_line < lines->size() &&
                !subclass_base_expression.empty()) {
                (*lines)[subclass_initializer_line] =
                    subclass_initializer_indent + class_name + " = " +
                    subclass_base_expression + ":CreateSubClass(\"" + class_name + "\")";
            }
            (*lines)[line_index + 1] =
                std::string(GetRenderedLuaIndent((*lines)[line_index + 1])) + "local " +
                modes_temp + " = " + modes_literal;

            std::vector<std::string> replacement_lines;
            replacement_lines.reserve(preserved_field_seeds.size() + 1);
            replacement_lines.push_back(assignment_indent + class_alias + ".MaintainedModes = " +
                                        modes_temp);
            for (const auto& [lhs, rhs] : preserved_field_seeds) {
                replacement_lines.push_back(assignment_indent + lhs + " = " + rhs);
            }
            replacement_lines.push_back(assignment_indent + method_target + " = " +
                                        std::string(TrimLuaWhitespace(rhs_parts[function_temp_index])));

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(banner_begin),
                         lines->begin() + static_cast<std::ptrdiff_t>(assignment_line + 1));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(banner_begin),
                          replacement_lines.begin(),
                          replacement_lines.end());

            const std::size_t function_start_line = banner_begin + replacement_lines.size() - 1;
            std::size_t dangling_line = lines->size();
            for (std::size_t probe = function_start_line + 1;
                 probe < lines->size() && probe < function_start_line + 256;
                 ++probe) {
                const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (trimmed.starts_with(",")) {
                    dangling_line = probe;
                    break;
                }
            }

            if (dangling_line < lines->size()) {
                std::size_t end_comment_line = lines->size();
                for (std::size_t probe = dangling_line; probe-- > function_start_line;) {
                    const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }
                    if (trimmed == "-- refii-suppressed-control: end") {
                        end_comment_line = probe;
                    }
                    break;
                }

                if (end_comment_line < lines->size()) {
                    std::string end_indent =
                        std::string(GetRenderedLuaIndent((*lines)[end_comment_line]));
                    for (std::size_t probe = end_comment_line; probe-- > function_start_line;) {
                        const std::string_view trimmed =
                            TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                        if (trimmed.empty()) {
                            continue;
                        }
                        if ((trimmed.starts_with("if ") &&
                             trimmed.find(" then") != std::string_view::npos) ||
                            (trimmed.starts_with("while ") &&
                             trimmed.find(" do") != std::string_view::npos) ||
                            (trimmed.starts_with("for ") &&
                             trimmed.find(" do") != std::string_view::npos) ||
                            trimmed == "do") {
                            end_indent = std::string(GetRenderedLuaIndent((*lines)[probe]));
                            break;
                        }
                    }
                    (*lines)[end_comment_line] = end_indent + "end";
                }

                std::size_t remove_end = dangling_line + 1;
                while (remove_end < lines->size()) {
                    const std::string_view trimmed = TrimLuaWhitespace((*lines)[remove_end]);
                    if (trimmed.empty()) {
                        ++remove_end;
                        continue;
                    }
                    if (trimmed == "-- refii-suppressed-control: end") {
                        ++remove_end;
                        continue;
                    }
                    break;
                }
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(dangling_line),
                             lines->begin() + static_cast<std::ptrdiff_t>(remove_end));

                for (std::size_t probe = dangling_line; probe < lines->size(); ++probe) {
                    bool probe_is_local = false;
                    std::string probe_lhs;
                    std::string probe_rhs;
                    if (ParseRenderedSingleAssignmentLine(
                            (*lines)[probe], &probe_is_local, &probe_lhs, &probe_rhs) &&
                        StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                            class_alias) {
                        break;
                    }

                    if (CountIdentifierUses((*lines)[probe], class_alias) == 0) {
                        continue;
                    }

                    const std::string normalized_probe_lhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr);
                    if (normalized_probe_lhs.starts_with(method_prefix)) {
                        const std::string probe_indent(GetRenderedLuaIndent((*lines)[probe]));
                        (*lines)[probe] =
                            probe_indent + class_name +
                            normalized_probe_lhs.substr(method_prefix.size() - 1) + " = " +
                            std::string(TrimLuaWhitespace(probe_rhs));
                        continue;
                    }

                    std::string rewritten_line;
                    if (!ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine(
                            (*lines)[probe], class_alias, class_name, &rewritten_line) ||
                        rewritten_line.empty() || rewritten_line == (*lines)[probe]) {
                        continue;
                    }

                    (*lines)[probe] = std::move(rewritten_line);
                }
            }

            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

