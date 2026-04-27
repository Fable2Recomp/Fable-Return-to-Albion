bool SimplifyRenderedLuaRedundantIpairsTableReseeds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto scope_is_prefix = [](const std::vector<std::size_t>& prefix,
                                    const std::vector<std::size_t>& value) {
        return prefix.size() <= value.size() &&
               std::equal(prefix.begin(), prefix.end(), value.begin());
    };

    const auto parse_ipairs_value_iterator = [](std::string_view line,
                                                std::string* value_name) {
        if (value_name == nullptr) {
            return false;
        }

        std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("for ") || !trimmed.ends_with(" do")) {
            return false;
        }
        trimmed = TrimLuaWhitespace(trimmed.substr(4, trimmed.size() - 7));
        const std::size_t in_pos = trimmed.find(" in ");
        if (in_pos == std::string_view::npos) {
            return false;
        }

        const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, in_pos));
        const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(in_pos + 4));
        if (!rhs.starts_with("ipairs(") || rhs.back() != ')') {
            return false;
        }

        std::vector<std::string> iter_names;
        std::size_t cursor = 0;
        while (cursor < lhs.size()) {
            const std::size_t comma = lhs.find(',', cursor);
            const std::string_view part = TrimLuaWhitespace(
                comma == std::string_view::npos ? lhs.substr(cursor)
                                                : lhs.substr(cursor, comma - cursor));
            if (part.empty() || !IsValidLuaIdentifier(part)) {
                return false;
            }
            iter_names.emplace_back(part);
            if (comma == std::string_view::npos) {
                break;
            }
            cursor = comma + 1;
        }
        if (iter_names.size() < 2) {
            return false;
        }
        *value_name = iter_names[1];
        return true;
    };

    return RunRenderedLuaCleanupPassLoop(__func__, 24, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);
        bool pass_changed = false;

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size()) {
                continue;
            }

            bool seed_is_local = false;
            std::string seed_lhs_expression;
            std::string seed_rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &seed_is_local,
                                                   &seed_lhs_expression,
                                                   &seed_rhs_expression)) {
                continue;
            }

            const std::string target_expression =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_lhs_expression), nullptr);
            const std::string normalized_seed_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs_expression), nullptr);
            if (!IsRenderedSafePrefixExpression(target_expression) ||
                normalized_seed_rhs != "{}") {
                continue;
            }

            std::size_t for_line = lines->size();
            std::string iterator_value_name;
            std::size_t lookahead_non_empty = 0;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() ||
                    !scope_is_prefix(scope_paths[line_index], scope_paths[probe])) {
                    break;
                }

                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                ++lookahead_non_empty;
                if (lookahead_non_empty > 12) {
                    break;
                }

                if (parse_ipairs_value_iterator((*lines)[probe], &iterator_value_name)) {
                    for_line = probe;
                    break;
                }

                if (CountIdentifierUses((*lines)[probe], target_expression) != 0) {
                    break;
                }

                bool probe_is_local = false;
                std::vector<std::string> probe_lhs_names;
                std::string probe_rhs_expression;
                if (ParseRenderedAssignmentLine((*lines)[probe],
                                                &probe_is_local,
                                                &probe_lhs_names,
                                                &probe_rhs_expression) &&
                    probe_lhs_names.size() == 1) {
                    const std::string probe_lhs = std::string(
                        TrimLuaWhitespace(probe_lhs_names.front()));
                    if (probe_is_local && IsRenderTemporaryName(probe_lhs)) {
                        continue;
                    }
                }

                if (trimmed.starts_with("local ")) {
                    continue;
                }
                break;
            }
            if (for_line >= lines->size() || iterator_value_name.empty()) {
                continue;
            }

            std::size_t reseed_line = lines->size();
            bool saw_index_assignment = false;
            const std::string expected_index_prefix =
                target_expression + "[" + iterator_value_name + "]";
            for (std::size_t probe = for_line + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }

                const std::vector<std::size_t>& loop_scope = scope_paths[for_line];
                const std::vector<std::size_t>& candidate_scope = scope_paths[probe];
                if (!scope_is_prefix(loop_scope, candidate_scope)) {
                    break;
                }

                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                if (trimmed == "end" && candidate_scope == loop_scope) {
                    break;
                }

                bool body_is_local = false;
                std::string body_lhs_expression;
                std::string body_rhs_expression;
                if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                       &body_is_local,
                                                       &body_lhs_expression,
                                                       &body_rhs_expression)) {
                    continue;
                }

                const std::string normalized_body_lhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(body_lhs_expression), nullptr);
                const std::string normalized_body_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(body_rhs_expression), nullptr);
                if (normalized_body_lhs == target_expression &&
                    normalized_body_rhs == "{}" &&
                    reseed_line >= lines->size()) {
                    reseed_line = probe;
                    continue;
                }

                if (reseed_line < lines->size() &&
                    normalized_body_lhs.starts_with(expected_index_prefix)) {
                    saw_index_assignment = true;
                    break;
                }
            }

            if (reseed_line >= lines->size() || !saw_index_assignment) {
                continue;
            }

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(reseed_line));
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

bool NormalizeRenderedLuaLoopActionBuilderAliases(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_set_current_action_callee = [](std::string_view callee) {
        callee = TrimLuaWhitespace(callee);
        return callee == "Action.SetCurrentAction" ||
               callee.ends_with(".SetCurrentAction") ||
               callee.ends_with(":SetCurrentAction");
    };
    const auto parse_set_current_action_target = [&](std::string_view line,
                                                     std::string* target_expression) -> bool {
        bool is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine(line,
                                               &is_local,
                                               &lhs_expression,
                                               &rhs_expression)) {
            return false;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine(rhs_expression, &callee, &arguments) ||
            arguments.size() < 2 || !is_set_current_action_callee(callee)) {
            return false;
        }

        const std::string target =
            StripRenderedWholeParentheses(TrimLuaWhitespace(arguments.back()), nullptr);
        if (!IsValidLuaIdentifier(target) || IsLuaKeyword(target) ||
            IsRenderTemporaryName(target)) {
            return false;
        }

        if (target_expression != nullptr) {
            *target_expression = target;
        }
        return true;
    };

    return RunRenderedLuaCleanupPassLoop(__func__, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableInitializerLine((*lines)[line_index],
                                                   &indent,
                                                   &lhs_prefix,
                                                   &table_name) ||
                !IsRenderTemporaryName(table_name)) {
                continue;
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            std::string target_expression;
            std::size_t target_line = lines->size();
            bool failed = false;
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                if (ParseRenderedIdentifierAliasAssignment((*lines)[probe],
                                                           table_name,
                                                           &target_expression) ||
                    IsAssignmentToIdentifier((*lines)[probe], table_name)) {
                    failed = true;
                    break;
                }

                if (CountIdentifierUses((*lines)[probe], table_name) != 0) {
                    failed = true;
                    break;
                }

                if (parse_set_current_action_target((*lines)[probe], &target_expression)) {
                    target_line = probe;
                    break;
                }
            }
            if (failed || target_line >= probe_limit || target_expression.empty()) {
                continue;
            }

            std::vector<std::size_t> builder_field_lines;
            bool saw_loop_type = false;
            for (std::size_t probe = line_index + 1; probe < target_line; ++probe) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }

                const std::size_t use_count = CountIdentifierUses((*lines)[probe], table_name);
                if (use_count == 0) {
                    continue;
                }

                const std::size_t first_non_space =
                    (*lines)[probe].find_first_not_of(" \t");
                if (first_non_space == std::string::npos ||
                    (*lines)[probe].compare(first_non_space,
                                            table_name.size(),
                                            table_name) != 0 ||
                    first_non_space + table_name.size() >= (*lines)[probe].size() ||
                    (*lines)[probe][first_non_space + table_name.size()] != '.') {
                    failed = true;
                    break;
                }

                builder_field_lines.push_back(probe);
                if (trimmed.starts_with(table_name + ".Type") &&
                    trimmed.find("EScriptableAction.LOOP") != std::string::npos) {
                    saw_loop_type = true;
                }
            }
            if (failed || builder_field_lines.size() < 3 || !saw_loop_type) {
                continue;
            }

            for (std::size_t probe = line_index; probe < probe_limit; ++probe) {
                if (probe == target_line) {
                    continue;
                }
                if (IsAssignmentToIdentifier((*lines)[probe], target_expression)) {
                    failed = true;
                    break;
                }
            }
            if (failed) {
                continue;
            }

            for (const std::size_t field_line : builder_field_lines) {
                const std::size_t first_non_space =
                    (*lines)[field_line].find_first_not_of(" \t");
                if (first_non_space == std::string::npos ||
                    (*lines)[field_line].compare(first_non_space,
                                                table_name.size(),
                                                table_name) != 0) {
                    failed = true;
                    break;
                }
                (*lines)[field_line].replace(first_non_space,
                                             table_name.size(),
                                             target_expression);
            }
            if (failed) {
                continue;
            }

            (*lines)[line_index] = indent + target_expression + " = {}";
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

bool NormalizeRenderedLuaForwardTableBuilderAliases(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto starts_with_table_field = [](std::string_view line,
                                            std::string_view table_name) -> bool {
        std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (trimmed.size() <= table_name.size() ||
            trimmed.substr(0, table_name.size()) != table_name) {
            return false;
        }
        const char next = trimmed[table_name.size()];
        return next == '.' || next == '[';
    };

    return RunRenderedLuaCleanupPassLoop(__func__, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableInitializerLine((*lines)[line_index],
                                                   &indent,
                                                   &lhs_prefix,
                                                   &table_name) ||
                !IsRenderTemporaryName(table_name)) {
                continue;
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            std::size_t alias_line = lines->size();
            std::string target_expression;
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                if (IsAssignmentToIdentifier((*lines)[probe], table_name)) {
                    break;
                }
                if (ParseRenderedIdentifierAliasAssignment((*lines)[probe],
                                                           table_name,
                                                           &target_expression)) {
                    alias_line = probe;
                    break;
                }
            }

            if (alias_line == lines->size() || target_expression.empty()) {
                continue;
            }

            bool saw_target_field = false;
            bool saw_any_field = false;
            bool failed = false;
            for (std::size_t probe = line_index + 1; probe < alias_line; ++probe) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    failed = true;
                    break;
                }
                if (starts_with_table_field(trimmed, table_name)) {
                    saw_any_field = true;
                    continue;
                }
                if (starts_with_table_field(trimmed, target_expression)) {
                    saw_any_field = true;
                    saw_target_field = true;
                    continue;
                }
                failed = true;
                break;
            }

            if (failed || !saw_any_field || !saw_target_field) {
                continue;
            }

            for (std::size_t probe = alias_line + 1; probe < probe_limit; ++probe) {
                if (IsAssignmentToIdentifier((*lines)[probe], table_name)) {
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

            for (std::size_t probe = line_index + 1; probe < alias_line; ++probe) {
                const std::size_t first_non_space =
                    (*lines)[probe].find_first_not_of(" \t");
                if (first_non_space == std::string::npos) {
                    failed = true;
                    break;
                }
                if ((*lines)[probe].compare(first_non_space,
                                            table_name.size(),
                                            table_name) == 0) {
                    (*lines)[probe].replace(first_non_space,
                                            table_name.size(),
                                            target_expression);
                }
            }
            if (failed) {
                continue;
            }

            (*lines)[line_index] = indent + target_expression + " = {}";
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(alias_line));
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

bool SimplifyRenderedLuaOrphanedTopLevelMemberBuilderAliases(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto extract_trailing_member_suffix = [](std::string_view expression,
                                                   std::string* suffix) -> bool {
        const auto is_identifier_char = [](char ch) {
            const unsigned char uch = static_cast<unsigned char>(ch);
            return std::isalnum(uch) != 0 || ch == '_';
        };

        expression = TrimLuaWhitespace(expression);
        const std::size_t dot = expression.find('.');
        if (dot == std::string_view::npos || dot + 1 >= expression.size()) {
            return false;
        }

        const std::string_view suffix_view = expression.substr(dot);
        std::size_t cursor = 0;
        while (cursor < suffix_view.size()) {
            if (suffix_view[cursor] != '.') {
                return false;
            }
            ++cursor;
            const std::size_t member_begin = cursor;
            while (cursor < suffix_view.size() && is_identifier_char(suffix_view[cursor])) {
                ++cursor;
            }
            if (cursor == member_begin) {
                return false;
            }
            if (cursor == suffix_view.size()) {
                break;
            }
            if (suffix_view[cursor] != '.') {
                return false;
            }
        }

        if (suffix_view.size() <= 1) {
            return false;
        }

        if (suffix != nullptr) {
            *suffix = std::string(suffix_view);
        }
        return true;
    };

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    bool changed = false;
    std::string anchored_target;
    std::string anchored_suffix;
    std::string anchored_indent;
    const auto is_nested_under_anchor = [](std::string_view candidate,
                                           std::string_view anchor) {
        return !anchor.empty() &&
               (candidate.starts_with(std::string(anchor) + ".") ||
                candidate.starts_with(std::string(anchor) + "["));
    };

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
            anchored_target.clear();
            anchored_suffix.clear();
            anchored_indent.clear();
            continue;
        }

        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed.empty()) {
            continue;
        }

        const std::string current_indent(GetRenderedLuaIndent((*lines)[line_index]));
        if (!anchored_indent.empty() && current_indent.size() < anchored_indent.size()) {
            anchored_target.clear();
            anchored_suffix.clear();
            anchored_indent.clear();
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

        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);

        if (IsRenderTemporaryName(normalized_lhs)) {
            if (!is_local) {
                anchored_target.clear();
                anchored_suffix.clear();
                anchored_indent.clear();
            }
            continue;
        }

        if (!anchored_target.empty()) {
            const std::size_t suffix_pos = normalized_lhs.find(anchored_suffix);
            if (suffix_pos != std::string::npos && suffix_pos > 0 &&
                suffix_pos + anchored_suffix.size() <= normalized_lhs.size()) {
                const std::string candidate_prefix = normalized_lhs.substr(0, suffix_pos);
                const bool suffix_matches_chain =
                    normalized_lhs.compare(suffix_pos, anchored_suffix.size(), anchored_suffix) == 0 &&
                    (suffix_pos + anchored_suffix.size() == normalized_lhs.size() ||
                     normalized_lhs[suffix_pos + anchored_suffix.size()] == '.' ||
                     normalized_lhs[suffix_pos + anchored_suffix.size()] == '[');
                if (suffix_matches_chain && IsRenderTemporaryName(candidate_prefix)) {
                    std::string rewritten;
                    if (ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine(
                            (*lines)[line_index],
                            candidate_prefix + anchored_suffix,
                            anchored_target,
                            &rewritten) &&
                        !rewritten.empty()) {
                        (*lines)[line_index] = std::move(rewritten);
                        changed = true;

                        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                               &is_local,
                                                               &lhs_expression,
                                                               &rhs_expression)) {
                            continue;
                        }
                    }
                }
            }
        }

        const std::string rewritten_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
        const std::string rewritten_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
        std::string literal_indent;
        std::string literal_prefix;
        std::string literal_target;
        if (ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                               &literal_indent,
                                               &literal_prefix,
                                               &literal_target) &&
            !IsRenderTemporaryName(literal_target)) {
            std::string member_suffix;
            if (extract_trailing_member_suffix(literal_target, &member_suffix)) {
                if (!anchored_target.empty() && current_indent == anchored_indent &&
                    is_nested_under_anchor(literal_target, anchored_target)) {
                    continue;
                }
                anchored_target = literal_target;
                anchored_suffix = std::move(member_suffix);
                anchored_indent = current_indent;
                continue;
            }
        }
        if (!is_local && rewritten_rhs == "{}" && IsRenderedSafePrefixExpression(rewritten_lhs)) {
            std::string member_suffix;
            if (extract_trailing_member_suffix(rewritten_lhs, &member_suffix)) {
                if (!anchored_target.empty() && current_indent == anchored_indent &&
                    is_nested_under_anchor(rewritten_lhs, anchored_target)) {
                    continue;
                }
                anchored_target = rewritten_lhs;
                anchored_suffix = std::move(member_suffix);
                anchored_indent = current_indent;
                continue;
            }
        }

        if (!anchored_target.empty() && current_indent == anchored_indent &&
            !IsRenderTemporaryName(rewritten_lhs) && rewritten_lhs != anchored_target &&
            !rewritten_lhs.starts_with(anchored_target + ".") &&
            !rewritten_lhs.starts_with(anchored_target + "[")) {
            anchored_target.clear();
            anchored_suffix.clear();
            anchored_indent.clear();
        }
    }

    return changed;
}

bool NormalizeRenderedLuaFreshTargetTableSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_inline_or_empty_table_literal = [](std::string_view rhs) {
        const std::string_view trimmed = TrimLuaWhitespace(rhs);
        return trimmed == "{}" || trimmed == "{ }" ||
               (trimmed.size() >= 2 && trimmed.front() == '{' && trimmed.back() == '}');
    };

    return RunRenderedLuaCleanupPassLoop(__func__, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression) ||
                is_local) {
                continue;
            }

            const std::string target_expression =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            const std::string temporary_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (target_expression.empty() || temporary_name.empty() ||
                IsRenderTemporaryName(target_expression) ||
                !IsRenderedSafePrefixExpression(target_expression) ||
                !IsRenderTemporaryName(temporary_name)) {
                continue;
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            std::size_t initializer_line = lines->size();
            bool initializer_is_empty_table = false;
            bool initializer_is_table_literal = false;
            for (std::size_t probe = line_index; probe > 0;) {
                --probe;

                bool earlier_is_local = false;
                std::string earlier_lhs_expression;
                std::string earlier_rhs_expression;
                if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                       &earlier_is_local,
                                                       &earlier_lhs_expression,
                                                       &earlier_rhs_expression)) {
                    continue;
                }

                const std::string earlier_lhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(earlier_lhs_expression),
                                                  nullptr);
                const std::string earlier_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(earlier_rhs_expression),
                                                  nullptr);
                if (earlier_lhs != temporary_name) {
                    continue;
                }

                if (earlier_rhs == "{}" || earlier_rhs == "{ }") {
                    initializer_line = probe;
                    initializer_is_empty_table = true;
                    initializer_is_table_literal = true;
                } else if (earlier_rhs == "{" || is_inline_or_empty_table_literal(earlier_rhs)) {
                    initializer_line = probe;
                    initializer_is_table_literal = true;
                }
                break;
            }
            if (initializer_line >= line_index) {
                continue;
            }

            bool saw_temporary_field_assignment = false;
            bool saw_distinct_temporary_use = false;
            bool saw_rhs_temporary_use = false;
            bool unsafe_before_alias = false;
            for (std::size_t probe = initializer_line + 1; probe < line_index; ++probe) {
                if (IsAssignmentToIdentifier((*lines)[probe], temporary_name)) {
                    unsafe_before_alias = true;
                    break;
                }

                std::string field_key;
                std::string field_rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (ParseRenderedTableFieldAssignment((*lines)[probe],
                                                      temporary_name,
                                                      &field_key,
                                                      &field_rhs_expression,
                                                      &rhs_is_function,
                                                      &array_index)) {
                    saw_temporary_field_assignment = true;
                    continue;
                }

                std::size_t rhs_use_count = 0;
                if (FindIdentifierUseInAssignmentRhs((*lines)[probe],
                                                    temporary_name,
                                                    nullptr,
                                                    &rhs_use_count) &&
                    rhs_use_count != 0) {
                    saw_rhs_temporary_use = true;
                    saw_distinct_temporary_use = true;
                }

                if (CountIdentifierUses((*lines)[probe], temporary_name) != 0) {
                    saw_distinct_temporary_use = true;
                }
            }
            const bool prior_payload_is_safe_to_drop =
                initializer_is_empty_table ? saw_temporary_field_assignment
                                           : initializer_is_table_literal;
            if (unsafe_before_alias || !prior_payload_is_safe_to_drop ||
                !saw_distinct_temporary_use) {
                continue;
            }

            bool saw_target_followup = false;
            bool unsafe_after_alias = false;
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                if (IsAssignmentToIdentifier((*lines)[probe], target_expression) ||
                    IsAssignmentToIdentifier((*lines)[probe], temporary_name)) {
                    break;
                }

                std::string field_key;
                std::string field_rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (ParseRenderedTableFieldAssignment((*lines)[probe],
                                                      target_expression,
                                                      &field_key,
                                                      &field_rhs_expression,
                                                      &rhs_is_function,
                                                      &array_index)) {
                    saw_target_followup = true;
                    continue;
                }

                if (TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                    continue;
                }

                if (CountIdentifierUses((*lines)[probe], temporary_name) != 0) {
                    unsafe_after_alias = true;
                    break;
                }

                if (CountIdentifierUses((*lines)[probe], target_expression) != 0) {
                    unsafe_after_alias = true;
                    break;
                }
            }
            if (!saw_target_followup || unsafe_after_alias) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();
            (*lines)[line_index] = indent + target_expression + " = {}" + std::string(comment);
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

bool NormalizeRenderedLuaStaleTargetTableAliases(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_table_literal_seed = [](std::string_view rhs) {
        const std::string_view trimmed = TrimLuaWhitespace(rhs);
        return trimmed == "{" || trimmed == "{}" || trimmed == "{ }" ||
               (trimmed.size() >= 2 && trimmed.front() == '{' && trimmed.back() == '}');
    };

    return RunRenderedLuaCleanupPassLoop(__func__, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression) ||
                is_local) {
                continue;
            }

            const std::string target_expression =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            const std::string temporary_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (target_expression.empty() || temporary_name.empty() ||
                IsRenderTemporaryName(target_expression) ||
                !IsRenderedSafePrefixExpression(target_expression) ||
                !IsRenderTemporaryName(temporary_name)) {
                continue;
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            bool saw_target_followup = false;
            bool unsafe_after_alias = false;
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                if (IsAssignmentToIdentifier((*lines)[probe], target_expression) ||
                    IsAssignmentToIdentifier((*lines)[probe], temporary_name)) {
                    break;
                }

                std::string field_key;
                std::string field_rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (ParseRenderedTableFieldAssignment((*lines)[probe],
                                                      target_expression,
                                                      &field_key,
                                                      &field_rhs_expression,
                                                      &rhs_is_function,
                                                      &array_index)) {
                    saw_target_followup = true;
                    continue;
                }

                if (TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                    continue;
                }

                if (CountIdentifierUses((*lines)[probe], temporary_name) != 0) {
                    unsafe_after_alias = true;
                    break;
                }
            }
            if (!saw_target_followup || unsafe_after_alias) {
                continue;
            }

            bool saw_initializer_table = false;
            bool saw_prior_rhs_use = false;
            for (std::size_t probe = line_index; probe > 0;) {
                --probe;

                bool earlier_is_local = false;
                std::string earlier_lhs_expression;
                std::string earlier_rhs_expression;
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                      &earlier_is_local,
                                                      &earlier_lhs_expression,
                                                      &earlier_rhs_expression)) {
                    const std::string earlier_lhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(earlier_lhs_expression),
                                                      nullptr);
                    const std::string earlier_rhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(earlier_rhs_expression),
                                                      nullptr);
                    if (earlier_lhs == temporary_name) {
                        saw_initializer_table = is_table_literal_seed(earlier_rhs);
                        break;
                    }

                    std::size_t rhs_use_count = 0;
                    if (FindIdentifierUseInAssignmentRhs((*lines)[probe],
                                                        temporary_name,
                                                        nullptr,
                                                        &rhs_use_count) &&
                        rhs_use_count != 0) {
                        saw_prior_rhs_use = true;
                    }
                }
            }

            if (!saw_initializer_table || !saw_prior_rhs_use) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();
            (*lines)[line_index] = indent + target_expression + " = {}" + std::string(comment);
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

bool SimplifyRenderedLuaStaleTargetLiteralOverwrites(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_table_literal_seed = [](std::string_view rhs) {
        const std::string_view trimmed = TrimLuaWhitespace(rhs);
        return trimmed == "{" || trimmed == "{}" || trimmed == "{ }" ||
               (trimmed.size() >= 2 && trimmed.front() == '{' && trimmed.back() == '}');
    };
    const auto next_nonempty_same_scope =
        [&](const std::vector<std::vector<std::size_t>>& scope_paths,
            std::size_t line_index) -> std::size_t {
            if (line_index >= scope_paths.size()) {
                return lines->size();
            }
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index]) {
                    continue;
                }
                if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                    return probe;
                }
            }
            return lines->size();
        };

    return RunRenderedLuaCleanupPassLoop(__func__, 32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
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
                                                   &rhs_expression) ||
                is_local) {
                continue;
            }

            const std::string target_expression =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            const std::string temporary_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (target_expression.empty() || IsRenderTemporaryName(target_expression) ||
                !IsRenderedSafePrefixExpression(target_expression) ||
                !IsRenderTemporaryName(temporary_name)) {
                continue;
            }

            const std::size_t next_line = next_nonempty_same_scope(scope_paths, line_index);
            if (next_line >= lines->size()) {
                continue;
            }

            bool next_is_local = false;
            std::string next_lhs_expression;
            std::string next_rhs_expression;
            std::string literal_indent;
            std::string literal_prefix;
            std::string literal_target;
            const bool next_is_literal_start =
                ParseRenderedTableLiteralStartLine((*lines)[next_line],
                                                  &literal_indent,
                                                  &literal_prefix,
                                                  &literal_target) &&
                literal_target == target_expression;
            const bool next_is_literal_assignment =
                ParseRenderedSingleAssignmentLine((*lines)[next_line],
                                                 &next_is_local,
                                                 &next_lhs_expression,
                                                 &next_rhs_expression) &&
                !next_is_local &&
                StripRenderedWholeParentheses(
                    TrimLuaWhitespace(next_lhs_expression), nullptr) == target_expression &&
                is_table_literal_seed(next_rhs_expression);
            if (!next_is_literal_start && !next_is_literal_assignment) {
                continue;
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            bool used_later = false;
            for (std::size_t probe = next_line + 1; probe < probe_limit; ++probe) {
                if (probe >= scope_paths.size() ||
                    scope_paths[probe] != scope_paths[line_index]) {
                    continue;
                }
                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], temporary_name) ||
                    IsAssignmentToIdentifier((*lines)[probe], temporary_name)) {
                    break;
                }
                if (CountIdentifierUses((*lines)[probe], temporary_name) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (used_later) {
                continue;
            }

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}
