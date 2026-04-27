bool LocalizeRenderedLuaFunctionLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_title_case_candidate = [](std::string_view name) {
        if (name.empty()) {
            return false;
        }

        const unsigned char first = static_cast<unsigned char>(name.front());
        if (std::isupper(first) == 0) {
            return false;
        }

        for (std::size_t i = 1; i < name.size(); ++i) {
            const unsigned char ch = static_cast<unsigned char>(name[i]);
            if (std::islower(ch) != 0) {
                return true;
            }
        }
        return false;
    };
    const auto looks_function_local_candidate = [&is_title_case_candidate](
                                                    std::string_view name) {
        if (name.empty()) {
            return false;
        }
        if (IsRenderTemporaryName(name)) {
            return true;
        }

        const unsigned char first = static_cast<unsigned char>(name.front());
        if (std::islower(first) != 0) {
            return true;
        }
        if (std::isupper(first) == 0) {
            return false;
        }

        return is_title_case_candidate(name);
    };

    bool changed = false;
    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (!IsRenderedFunctionStartLine((*lines)[line_index])) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, line_index);
        if (function_end >= lines->size() || function_end <= line_index) {
            continue;
        }
        if (IsRenderedSyntheticRootWrapperFunction(*lines, line_index, function_end)) {
            continue;
        }

        std::unordered_set<std::string> seen_names;
        SeedRenderedFunctionParameters((*lines)[line_index], &seen_names);
        const auto scope_path_is_prefix = [](const std::vector<std::size_t>& prefix,
                                             const std::vector<std::size_t>& candidate) {
            return prefix.size() <= candidate.size() &&
                   std::equal(prefix.begin(), prefix.end(), candidate.begin());
        };
        const auto count_later_uses = [&](std::size_t start, std::string_view name) {
            std::size_t count = 0;
            for (std::size_t future = start + 1; future < function_end; ++future) {
                if (IsRenderedFunctionStartLine((*lines)[future])) {
                    const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, future);
                    if (nested_end >= lines->size() || nested_end <= future) {
                        break;
                    }
                    future = nested_end;
                    continue;
                }
                count += CountIdentifierUses((*lines)[future], name);
            }
            return count;
        };
        const auto escapes_assignment_scope = [&](std::size_t start, std::string_view name) {
            if (start >= scope_paths.size()) {
                return false;
            }

            const std::vector<std::size_t>& declaration_scope = scope_paths[start];
            for (std::size_t future = start + 1;
                 future < function_end && future < scope_paths.size();
                 ++future) {
                if (IsRenderedFunctionStartLine((*lines)[future])) {
                    const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, future);
                    if (nested_end >= lines->size() || nested_end <= future) {
                        break;
                    }
                    future = nested_end;
                    continue;
                }

                if (CountIdentifierUses((*lines)[future], name) == 0) {
                    continue;
                }

                if (!scope_path_is_prefix(declaration_scope, scope_paths[future])) {
                    return true;
                }
            }
            return false;
        };
        const auto count_later_assignments = [&](std::size_t start, std::string_view name) {
            std::size_t count = 0;
            for (std::size_t future = start + 1; future < function_end; ++future) {
                if (IsRenderedFunctionStartLine((*lines)[future])) {
                    const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, future);
                    if (nested_end >= lines->size() || nested_end <= future) {
                        break;
                    }
                    future = nested_end;
                    continue;
                }

                bool future_is_local = false;
                std::vector<std::string> future_lhs_names;
                std::string future_rhs_expression;
                if (!ParseRenderedAssignmentLine((*lines)[future],
                                                &future_is_local,
                                                &future_lhs_names,
                                                &future_rhs_expression)) {
                    continue;
                }

                if (std::find(future_lhs_names.begin(), future_lhs_names.end(), name) !=
                    future_lhs_names.end()) {
                    ++count;
                }
            }
            return count;
        };

        for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
            if (IsRenderedFunctionStartLine((*lines)[probe])) {
                const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, probe);
                if (nested_end >= lines->size() || nested_end <= probe) {
                    break;
                }
                probe = nested_end;
                continue;
            }

            if (StripLuaLineComment((*lines)[probe]).find('{') != std::string_view::npos) {
                const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, probe);
                if (table_end >= lines->size()) {
                    break;
                }
                if (table_end > probe) {
                    probe = table_end;
                    continue;
                }
            }

            std::vector<std::string> local_names;
            if (ParseRenderedLocalDeclarationLine((*lines)[probe], &local_names)) {
                for (const std::string& local_name : local_names) {
                    seen_names.insert(local_name);
                }
                continue;
            }

            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!ParseRenderedAssignmentLine((*lines)[probe],
                                            &is_local,
                                            &lhs_names,
                                            &rhs_expression)) {
                continue;
            }

            if (is_local) {
                for (const std::string& name : lhs_names) {
                    seen_names.insert(name);
                }
                continue;
            }

            const std::string normalized_rhs =
                std::string(TrimLuaWhitespace(rhs_expression));
            if (lhs_names.size() > 1) {
                bool can_localize_multi = normalized_rhs == "nil";
                for (const std::string& name : lhs_names) {
                    if (!looks_function_local_candidate(name) ||
                        count_later_uses(probe, name) == 0 ||
                        escapes_assignment_scope(probe, name) ||
                        !ShouldEmitLocalForRenderedIdentifierTarget(*lines, probe, name)) {
                        can_localize_multi = false;
                    }
                    seen_names.insert(name);
                }
                if (!can_localize_multi) {
                    continue;
                }

                const std::size_t first_non_space = (*lines)[probe].find_first_not_of(" \t");
                if (first_non_space == std::string::npos) {
                    continue;
                }

                const std::string indent = (*lines)[probe].substr(0, first_non_space);
                std::ostringstream rebuilt;
                rebuilt << indent << "local ";
                for (std::size_t i = 0; i < lhs_names.size(); ++i) {
                    if (i != 0) {
                        rebuilt << ", ";
                    }
                    rebuilt << lhs_names[i];
                }
                (*lines)[probe] = rebuilt.str();
                changed = true;
                continue;
            }

            const std::string& name = lhs_names.front();
            if (!looks_function_local_candidate(name)) {
                seen_names.insert(name);
                continue;
            }
            if (!seen_names.insert(name).second) {
                continue;
            }
            if (is_title_case_candidate(name) &&
                (count_later_uses(probe, name) < 2 ||
                 count_later_assignments(probe, name) == 0)) {
                continue;
            }
            if (escapes_assignment_scope(probe, name)) {
                continue;
            }
            if (!ShouldEmitLocalForRenderedIdentifierTarget(*lines, probe, name)) {
                continue;
            }

            const std::size_t first_non_space = (*lines)[probe].find_first_not_of(" \t");
            if (first_non_space == std::string::npos) {
                continue;
            }

            (*lines)[probe].insert(first_non_space, "local ");
            changed = true;
        }
    }

    return changed;
}

bool RewriteTrailingRenderedTableBuilderTarget(std::vector<std::string>* lines,
                                               std::string_view table_name,
                                               std::string_view target_expression) {
    if (lines == nullptr || lines->empty() || table_name.empty() || target_expression.empty()) {
        return false;
    }

    for (std::size_t start = lines->size(); start-- > 0;) {
        std::string indent;
        std::string lhs_prefix;
        std::string parsed_name;
        if (!ParseRenderedTableInitializerLine((*lines)[start], &indent, &lhs_prefix, &parsed_name) ||
            parsed_name != table_name) {
            continue;
        }

        std::size_t cursor = start + 1;
        std::size_t entry_count = 0;
        while (cursor < lines->size()) {
            const std::string_view trimmed = TrimLuaWhitespace((*lines)[cursor]);
            if (trimmed.empty()) {
                return false;
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
                return false;
            }

            const std::size_t first_non_space = (*lines)[cursor].find_first_not_of(" \t");
            if (first_non_space == std::string::npos ||
                (*lines)[cursor].compare(first_non_space, table_name.size(), table_name) != 0) {
                return false;
            }
            (*lines)[cursor].replace(first_non_space,
                                     table_name.size(),
                                     std::string(target_expression));

            if (rhs_is_function) {
                const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, cursor);
                if (function_end >= lines->size() || function_end < cursor) {
                    return false;
                }
                cursor = function_end + 1;
            } else if (rhs_expression == "{") {
                const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, cursor);
                if (table_end >= lines->size() || table_end < cursor) {
                    return false;
                }
                cursor = table_end + 1;
            } else {
                ++cursor;
            }
            ++entry_count;
        }

        if (entry_count == 0) {
            return false;
        }

        const std::size_t first_non_space = (*lines)[start].find_first_not_of(" \t");
        const std::string start_indent =
            first_non_space == std::string::npos
                ? std::string()
                : std::string((*lines)[start].substr(0, first_non_space));
        // This helper is only used when a bytecode-backed SETGLOBAL retargets a trailing
        // table builder. Preserve that exported ownership instead of inventing a chunk-local.
        (*lines)[start] = start_indent + std::string(target_expression) + " = {}";
        return true;
    }

    return false;
}

bool ParseRenderedIdentifierAliasAssignment(std::string_view line,
                                            std::string_view rhs_identifier,
                                            std::string* lhs_expression) {
    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty() || trimmed.starts_with("local ")) {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs.empty() || lhs.find(',') != std::string_view::npos || rhs != rhs_identifier ||
        lhs == rhs_identifier) {
        return false;
    }

    if (lhs_expression != nullptr) {
        *lhs_expression = std::string(lhs);
    }
    return true;
}

bool ParseRenderedTopLevelMethodAssignment(std::string_view line,
                                           std::string* table_name,
                                           std::string* method_name) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty() || trimmed.starts_with("local ")) {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (!rhs.starts_with("function(")) {
        return false;
    }

    const std::size_t dot = lhs.find('.');
    if (dot == std::string_view::npos || lhs.find('.', dot + 1) != std::string_view::npos) {
        return false;
    }

    const std::string_view parsed_table_name = TrimLuaWhitespace(lhs.substr(0, dot));
    const std::string_view parsed_method_name = TrimLuaWhitespace(lhs.substr(dot + 1));
    if (!IsValidLuaIdentifier(parsed_table_name) || !IsValidLuaIdentifier(parsed_method_name)) {
        return false;
    }

    if (table_name != nullptr) {
        *table_name = std::string(parsed_table_name);
    }
    if (method_name != nullptr) {
        *method_name = std::string(parsed_method_name);
    }
    return true;
}

bool RestoreRenderedLuaBaseObjectInitializers(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    std::size_t first_non_empty = lines->size();
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index])).empty()) {
            first_non_empty = line_index;
            break;
        }
    }
    if (first_non_empty == lines->size()) {
        return false;
    }

    std::size_t body_start = first_non_empty;
    const std::string_view first_trimmed =
        TrimLuaWhitespace(StripLuaLineComment((*lines)[first_non_empty]));
    if (first_trimmed.starts_with("function(")) {
        body_start = lines->size();
        for (std::size_t line_index = first_non_empty + 1; line_index < lines->size(); ++line_index) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            if (!trimmed.empty()) {
                body_start = line_index;
                break;
            }
        }
        if (body_start == lines->size()) {
            return false;
        }
    }

    std::string table_name;
    std::string first_method_name;
    if (!ParseRenderedTopLevelMethodAssignment((*lines)[body_start],
                                               &table_name,
                                               &first_method_name) ||
        first_method_name != "new") {
        return false;
    }

    const std::string base_object_expression = "BaseObjects." + table_name;
    bool saw_initializer = false;
    bool saw_supporting_method = false;
    std::size_t scanned_non_empty = 0;
    for (std::size_t line_index = body_start;
         line_index < lines->size() && scanned_non_empty < 12;
         ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed.empty()) {
            continue;
        }
        ++scanned_non_empty;

        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (ParseRenderedAssignmentLine((*lines)[line_index],
                                        &assign_is_local,
                                        &lhs_names,
                                        &rhs_expression) &&
            lhs_names.size() == 1) {
            const std::string& lhs_name = lhs_names.front();
            if (lhs_name == table_name || lhs_name == base_object_expression) {
                saw_initializer = true;
                break;
            }
        }

        std::string seen_table_name;
        std::string seen_method_name;
        if (ParseRenderedTopLevelMethodAssignment((*lines)[line_index],
                                                  &seen_table_name,
                                                  &seen_method_name) &&
            seen_table_name == table_name &&
            (seen_method_name == "Create" || seen_method_name == "GetModeType" ||
             seen_method_name == "GetModeInterface" || seen_method_name == "OnActivate")) {
            saw_supporting_method = true;
        }
    }

    if (saw_initializer || !saw_supporting_method) {
        return false;
    }

    const std::size_t first_non_space = (*lines)[body_start].find_first_not_of(" \t");
    const std::string indent =
        first_non_space == std::string::npos
            ? std::string()
            : std::string((*lines)[body_start].substr(0, first_non_space));
    lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(body_start),
                  indent + base_object_expression + " = {}");
    return true;
}

bool NormalizeRenderedLuaNilTablePlaceholders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_nested_array_slot_field_assignment =
        [](std::string_view line, std::string_view table_expression, int* array_index_out) {
            if (array_index_out != nullptr) {
                *array_index_out = -1;
            }

            std::string_view trimmed = TrimLuaWhitespace(line);
            if (trimmed.size() < table_expression.size() ||
                trimmed.substr(0, table_expression.size()) != table_expression) {
                return false;
            }
            trimmed.remove_prefix(table_expression.size());
            trimmed = TrimLuaWhitespace(trimmed);
            if (trimmed.empty() || trimmed.front() != '[') {
                return false;
            }

            const std::size_t close = trimmed.find(']');
            if (close == std::string_view::npos || close <= 1) {
                return false;
            }

            const std::string_view index_text = TrimLuaWhitespace(trimmed.substr(1, close - 1));
            if (index_text.empty()) {
                return false;
            }
            int parsed_index = 0;
            for (char ch : index_text) {
                if (std::isdigit(static_cast<unsigned char>(ch)) == 0) {
                    return false;
                }
                parsed_index = parsed_index * 10 + (ch - '0');
            }
            if (parsed_index <= 0) {
                return false;
            }

            std::string_view suffix = TrimLuaWhitespace(trimmed.substr(close + 1));
            if (suffix.empty() || (suffix.front() != '.' && suffix.front() != '[')) {
                return false;
            }
            const std::size_t equal = suffix.find('=');
            if (equal == std::string_view::npos || equal == 0) {
                return false;
            }

            if (array_index_out != nullptr) {
                *array_index_out = parsed_index;
            }
            return true;
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

            const std::string table_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (!IsRenderedSafePrefixExpression(table_name) || normalized_rhs != "nil") {
                continue;
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            bool saw_field_assignment = false;
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                const std::string_view trimmed_line =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed_line.empty()) {
                    continue;
                }

                if (IsAssignmentToIdentifier((*lines)[probe], table_name)) {
                    break;
                }

                std::string field_key;
                std::string field_rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (ParseRenderedTableFieldAssignment((*lines)[probe],
                                                     table_name,
                                                     &field_key,
                                                     &field_rhs_expression,
                                                     &rhs_is_function,
                                                     &array_index)) {
                    saw_field_assignment = true;
                }
                break;
            }

            if (!saw_field_assignment) {
                continue;
            }

            const std::size_t first_non_space =
                (*lines)[line_index].find_first_not_of(" \t");
            const std::string indent =
                first_non_space == std::string::npos
                    ? std::string()
                    : std::string((*lines)[line_index].substr(0, first_non_space));
            (*lines)[line_index] = indent + table_name + " = {}";
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
                bool is_local = false;
                std::string lhs_expression;
                std::string rhs_expression;
                if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                       &is_local,
                                                       &lhs_expression,
                                                       &rhs_expression)) {
                    continue;
                }

                const std::string table_expression =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
                const std::string normalized_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
                if (!IsRenderedSafePrefixExpression(table_expression) ||
                    normalized_rhs != "{}") {
                    continue;
                }

                const std::size_t scope_end =
                    FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
                const std::size_t probe_limit =
                    scope_end < lines->size() ? scope_end + 1 : lines->size();

                std::unordered_set<int> initialized_slots;
                bool inserted = false;
                for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                    if (IsAssignmentToIdentifier((*lines)[probe], table_expression)) {
                        break;
                    }

                    std::string direct_field_key;
                    std::string direct_rhs_expression;
                    bool direct_rhs_is_function = false;
                    int direct_array_index = -1;
                    if (ParseRenderedTableFieldAssignment((*lines)[probe],
                                                          table_expression,
                                                          &direct_field_key,
                                                          &direct_rhs_expression,
                                                          &direct_rhs_is_function,
                                                          &direct_array_index)) {
                        if (direct_array_index > 0) {
                            initialized_slots.insert(direct_array_index);
                        }
                        continue;
                    }

                    int nested_array_index = -1;
                    if (!parse_nested_array_slot_field_assignment((*lines)[probe],
                                                                  table_expression,
                                                                  &nested_array_index) ||
                        nested_array_index <= 0 ||
                        initialized_slots.contains(nested_array_index)) {
                        continue;
                    }

                    const std::string indent(GetRenderedLuaIndent((*lines)[probe]));
                    lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(probe),
                                  indent + table_expression + "[" +
                                      std::to_string(nested_array_index) + "] = {}");
                    initialized_slots.insert(nested_array_index);
                    inserted = true;
                    pass_changed = true;
                    break;
                }

                if (inserted) {
                    break;
                }
            }
        }

        return pass_changed;
    });
}

bool NormalizeRenderedLuaMixedTableBuilderAliases(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    return RunRenderedLuaCleanupPassLoop(__func__, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            const std::string_view trimmed_line =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
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

            std::vector<std::size_t> temporary_field_lines;
            std::size_t cursor = line_index + 1;
            std::size_t field_count = 0;
            bool failed = false;
            while (cursor < alias_line) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor]));
                if (trimmed.empty()) {
                    ++cursor;
                    continue;
                }

                std::string field_key;
                std::string rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                bool parsed_temporary = ParseRenderedTableFieldAssignment((*lines)[cursor],
                                                                         table_name,
                                                                         &field_key,
                                                                         &rhs_expression,
                                                                         &rhs_is_function,
                                                                         &array_index);
                bool parsed_target = false;
                if (!parsed_temporary) {
                    parsed_target = ParseRenderedTableFieldAssignment((*lines)[cursor],
                                                                      target_expression,
                                                                      &field_key,
                                                                      &rhs_expression,
                                                                      &rhs_is_function,
                                                                      &array_index);
                }

                if (!parsed_temporary && !parsed_target) {
                    if (CountIdentifierUses((*lines)[cursor], table_name) != 0 ||
                        CountIdentifierUses((*lines)[cursor], target_expression) != 0) {
                        failed = true;
                        break;
                    }
                    ++cursor;
                    continue;
                }

                std::size_t next_cursor = cursor + 1;
                if (rhs_is_function) {
                    const std::size_t function_end =
                        FindRenderedFunctionBlockEnd(*lines, cursor);
                    if (function_end >= lines->size() || function_end < cursor ||
                        function_end >= alias_line) {
                        failed = true;
                        break;
                    }
                    next_cursor = function_end + 1;
                } else if (rhs_expression == "{") {
                    const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, cursor);
                    if (table_end >= lines->size() || table_end < cursor ||
                        table_end >= alias_line) {
                        failed = true;
                        break;
                    }
                    next_cursor = table_end + 1;
                }

                if (parsed_temporary) {
                    temporary_field_lines.push_back(cursor);
                }

                ++field_count;
                cursor = next_cursor;
            }

            if (failed || field_count == 0) {
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

            for (const std::size_t field_line : temporary_field_lines) {
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
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(alias_line));
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

