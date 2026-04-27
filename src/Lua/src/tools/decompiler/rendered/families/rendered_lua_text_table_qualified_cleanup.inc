bool SimplifyRenderedLuaQualifiedTableLiteralArrayTails(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines->size(); ++i) {
            if (!TrimLuaWhitespace((*lines)[i]).empty()) {
                return i;
            }
        }
        return lines->size();
    };

    bool changed = false;
    constexpr std::string_view kStepName =
        "SimplifyRenderedLuaQualifiedTableLiteralArrayTails";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                    &indent,
                                                    &lhs_prefix,
                                                    &table_name)) {
                continue;
            }

            if (!IsValidLuaIdentifier(table_name) &&
                table_name.find('.') == std::string::npos &&
                table_name.find('[') == std::string::npos) {
                continue;
            }

            const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, line_index);
            if (table_end >= lines->size() || table_end <= line_index + 1) {
                continue;
            }

            int expected_array_index = 1;
            bool failed = false;
            std::size_t cursor = next_nonempty(line_index + 1);
            while (cursor < table_end) {
                const std::string_view trimmed = TrimLuaWhitespace((*lines)[cursor]);
                if (trimmed.empty()) {
                    cursor = next_nonempty(cursor + 1);
                    continue;
                }

                std::size_t entry_end = cursor;
                if (trimmed == "{") {
                    entry_end = FindRenderedTableLiteralEnd(*lines, cursor);
                } else if (trimmed.starts_with("function(") ||
                           trimmed.starts_with("function ")) {
                    entry_end = FindRenderedFunctionBlockEnd(*lines, cursor);
                } else if (trimmed.find('=') != std::string_view::npos) {
                    failed = true;
                    break;
                }

                if (entry_end >= table_end || entry_end < cursor) {
                    failed = true;
                    break;
                }

                ++expected_array_index;
                cursor = next_nonempty(entry_end + 1);
            }

            if (failed) {
                continue;
            }

            std::vector<std::pair<std::size_t, std::size_t>> erase_ranges;
            std::vector<std::string> appended_lines;
            std::size_t probe = next_nonempty(table_end + 1);
            while (probe < lines->size()) {
                if (GetRenderedLuaIndent((*lines)[probe]) != indent) {
                    break;
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
                                                      &array_index) ||
                    array_index != expected_array_index) {
                    break;
                }

                std::size_t entry_end = probe;
                const bool rhs_is_table_literal = rhs_expression == "{";
                if (rhs_is_function) {
                    entry_end = FindRenderedFunctionBlockEnd(*lines, probe);
                } else if (rhs_is_table_literal) {
                    entry_end = FindRenderedTableLiteralEnd(*lines, probe);
                } else {
                    int rhs_paren_balance = 0;
                    int rhs_brace_balance = 0;
                    if (HasPositiveLuaExpressionBalance(rhs_expression,
                                                       &rhs_paren_balance,
                                                       &rhs_brace_balance)) {
                        break;
                    }
                }

                if (entry_end >= lines->size() || entry_end < probe) {
                    break;
                }

                if (rhs_is_table_literal) {
                    for (std::size_t body_line = probe + 1; body_line < entry_end; ++body_line) {
                        if (CountIdentifierUses((*lines)[body_line], table_name) != 0) {
                            failed = true;
                            break;
                        }
                    }
                    if (failed) {
                        break;
                    }
                }

                if (rhs_is_function) {
                    appended_lines.push_back(indent + "  " + rhs_expression);
                    for (std::size_t body_line = probe + 1; body_line <= entry_end; ++body_line) {
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == entry_end) {
                            moved_line += ",";
                        }
                        appended_lines.push_back(std::move(moved_line));
                    }
                } else if (rhs_is_table_literal) {
                    const bool is_empty_placeholder_literal = entry_end == probe + 1;
                    if (is_empty_placeholder_literal) {
                        appended_lines.push_back(indent + "  {},");
                    } else {
                        appended_lines.push_back(indent + "  {");
                        for (std::size_t body_line = probe + 1; body_line <= entry_end;
                             ++body_line) {
                            std::string moved_line = "  " + (*lines)[body_line];
                            if (body_line == entry_end) {
                                moved_line += ",";
                            }
                            appended_lines.push_back(std::move(moved_line));
                        }
                    }
                } else {
                    appended_lines.push_back(indent + "  " + rhs_expression + ",");
                }

                erase_ranges.emplace_back(probe, entry_end);
                ++expected_array_index;
                probe = next_nonempty(entry_end + 1);
            }

            if (failed || appended_lines.empty()) {
                continue;
            }

            for (auto it = erase_ranges.rbegin(); it != erase_ranges.rend(); ++it) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(it->first),
                             lines->begin() + static_cast<std::ptrdiff_t>(it->second + 1));
            }
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(table_end),
                          appended_lines.begin(),
                          appended_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaQualifiedTableBuilders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr std::string_view kStepName = "SimplifyRenderedLuaQualifiedTableBuilders";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
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

            if (IsValidLuaIdentifier(table_name) ||
                (table_name.find('.') == std::string::npos &&
                 table_name.find('[') == std::string::npos)) {
                continue;
            }

            std::vector<std::string> replacement_lines;
            replacement_lines.push_back(lhs_prefix + "{");

            std::size_t cursor = line_index + 1;
            std::size_t entry_count = 0;
            int expected_array_index = 1;
            bool failed = false;
            while (cursor < lines->size()) {
                const std::string_view trimmed = TrimLuaWhitespace((*lines)[cursor]);
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

                const bool rhs_is_table_literal = rhs_expression == "{";
                int rhs_paren_balance = 0;
                int rhs_brace_balance = 0;
                if (rhs_is_function ||
                    HasPositiveLuaExpressionBalance(rhs_expression,
                                                   &rhs_paren_balance,
                                                   &rhs_brace_balance)) {
                    if (!rhs_is_table_literal) {
                        failed = true;
                        break;
                    }
                }

                const bool is_array_entry =
                    array_index > 0 && array_index == expected_array_index;
                if (rhs_is_table_literal) {
                    const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, cursor);
                    if (table_end >= lines->size() || table_end < cursor) {
                        failed = true;
                        break;
                    }
                    for (std::size_t body_line = cursor + 1; body_line < table_end; ++body_line) {
                        if (CountIdentifierUses((*lines)[body_line], table_name) != 0) {
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
                    for (std::size_t body_line = cursor + 1; body_line <= table_end; ++body_line) {
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == table_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = table_end + 1;
                } else {
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  " + rhs_expression + ",");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = " +
                                                    rhs_expression + ",");
                    }
                    ++cursor;
                }
                if (is_array_entry) {
                    ++expected_array_index;
                }
                ++entry_count;
            }

            if (failed || entry_count < 2) {
                continue;
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

bool ExpandRenderedLuaLargeQualifiedTableLiterals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    constexpr std::size_t kMinExpandedEntryCount = 8;
    constexpr std::size_t kMinExpandedArrayLikeEntryCount = 3;

    bool changed = false;
    constexpr std::string_view kStepName = "ExpandRenderedLuaLargeQualifiedTableLiterals";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                    &indent,
                                                    &lhs_prefix,
                                                    &table_name)) {
                continue;
            }

            if (IsValidLuaIdentifier(table_name) ||
                (table_name.find('.') == std::string::npos &&
                 table_name.find('[') == std::string::npos)) {
                continue;
            }

            const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, line_index);
            if (table_end >= lines->size() || table_end <= line_index + 1) {
                continue;
            }

            struct LiteralEntry {
                std::string access_suffix;
                std::string rhs_expression;
            };

            std::vector<LiteralEntry> entries;
            bool all_entries_are_nonstring_bracket_keys = true;
            bool failed = false;
            for (std::size_t body_line = line_index + 1; body_line < table_end; ++body_line) {
                std::string_view trimmed = TrimLuaWhitespace((*lines)[body_line]);
                if (trimmed.empty() || !trimmed.ends_with(",")) {
                    failed = true;
                    break;
                }
                trimmed.remove_suffix(1);
                trimmed = TrimLuaWhitespace(trimmed);

                const std::size_t equal = trimmed.find('=');
                if (equal == std::string_view::npos) {
                    failed = true;
                    break;
                }

                const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
                const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
                if (lhs.empty() || rhs.empty()) {
                    failed = true;
                    break;
                }

                std::string access_suffix;
                if (IsValidLuaIdentifier(lhs)) {
                    access_suffix = "." + std::string(lhs);
                    all_entries_are_nonstring_bracket_keys = false;
                } else if (lhs.front() == '[' && lhs.back() == ']') {
                    access_suffix = std::string(lhs);
                    const std::string_view bracket_key =
                        TrimLuaWhitespace(lhs.substr(1, lhs.size() - 2));
                    if (bracket_key.empty() ||
                        ((bracket_key.front() == '"' && bracket_key.back() == '"') ||
                         (bracket_key.front() == '\'' && bracket_key.back() == '\''))) {
                        all_entries_are_nonstring_bracket_keys = false;
                    }
                } else {
                    failed = true;
                    break;
                }

                int rhs_paren_balance = 0;
                int rhs_brace_balance = 0;
                if (rhs == "{" ||
                    (rhs.size() >= 9 && rhs.substr(0, 9) == std::string_view("function(")) ||
                    HasPositiveLuaExpressionBalance(rhs,
                                                   &rhs_paren_balance,
                                                   &rhs_brace_balance)) {
                    failed = true;
                    break;
                }

                entries.push_back({std::move(access_suffix), std::string(rhs)});
            }

            const bool expand_array_like_literal =
                all_entries_are_nonstring_bracket_keys &&
                entries.size() >= kMinExpandedArrayLikeEntryCount;
            if (failed ||
                (!expand_array_like_literal && entries.size() < kMinExpandedEntryCount)) {
                continue;
            }

            std::vector<std::string> replacement_lines;
            replacement_lines.reserve(entries.size() + 1);
            replacement_lines.push_back(lhs_prefix + "{}");
            for (const LiteralEntry& entry : entries) {
                replacement_lines.push_back(indent + table_name + entry.access_suffix + " = " +
                                            entry.rhs_expression);
            }

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(table_end) + 1);
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

bool SimplifyRenderedLuaReturnedTableBuilders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr std::string_view kStepName = "SimplifyRenderedLuaReturnedTableBuilders";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            bool parsed_initializer =
                ParseRenderedTableInitializerLine((*lines)[line_index],
                                                 &indent,
                                                 &lhs_prefix,
                                                 &table_name);
            bool parsed_literal = false;
            std::size_t literal_end = lines->size();
            if (!parsed_initializer) {
                parsed_literal = ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                                    &indent,
                                                                    &lhs_prefix,
                                                                    &table_name);
                if (!parsed_literal) {
                    continue;
                }
                literal_end = FindRenderedTableLiteralEnd(*lines, line_index);
                if (literal_end >= lines->size() || literal_end <= line_index) {
                    continue;
                }
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            std::vector<std::string> prefix_lines;
            std::vector<std::string> replacement_lines;
            std::size_t cursor = parsed_literal ? literal_end + 1 : line_index + 1;
            std::size_t entry_count = 0;
            bool found_first_field = false;
            bool failed = false;
            int expected_array_index = 1;

            if (parsed_literal) {
                replacement_lines.push_back(indent + "return {");
                for (std::size_t body_line = line_index + 1; body_line < literal_end; ++body_line) {
                    replacement_lines.push_back((*lines)[body_line]);
                    ++entry_count;
                }
            }

            while (cursor < probe_limit) {
                if (IsRenderedExactReturnIdentifier((*lines)[cursor], table_name)) {
                    break;
                }

                if (IsAssignmentToIdentifier((*lines)[cursor], table_name)) {
                    failed = true;
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
                    if (TryParseRenderedTemporaryBackedTableField(*lines,
                                                                  cursor,
                                                                  table_name,
                                                                  &field_key,
                                                                  &rhs_expression,
                                                                  &rhs_is_function,
                                                                  &array_index,
                                                                  &consumed_lines)) {
                        found_first_field = true;
                    } else {
                        if (!TryParseRenderedConditionalTableField(*lines,
                                                                   cursor,
                                                                   table_name,
                                                                   &conditional_expression,
                                                                   &field_key,
                                                                   &rhs_expression,
                                                                   &else_expression,
                                                                   &array_index,
                                                                   &consumed_lines)) {
                            if (found_first_field) {
                                failed = true;
                                break;
                            }
                            if (CountIdentifierUses((*lines)[cursor], table_name) != 0) {
                                failed = true;
                                break;
                            }
                            prefix_lines.push_back((*lines)[cursor]);
                            ++cursor;
                            continue;
                        }
                    }
                }

                found_first_field = true;
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

                if (!rhs_is_function && CountIdentifierUses(rhs_expression, table_name) != 0) {
                    failed = true;
                    break;
                }

                if (!conditional_expression.empty()) {
                    if (entry_count == 0) {
                        replacement_lines.push_back(indent + "return {");
                    }
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  (function()");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key +
                                                    " = (function()");
                    }
                    replacement_lines.push_back(indent + "    if " + conditional_expression +
                                                " then");
                    replacement_lines.push_back(indent + "      return " + rhs_expression);
                    replacement_lines.push_back(indent + "    else");
                    replacement_lines.push_back(indent + "      return " + else_expression);
                    replacement_lines.push_back(indent + "    end");
                    replacement_lines.push_back(indent + "  end)(),");
                    cursor += consumed_lines;
                } else if (rhs_is_function) {
                    const std::size_t function_end =
                        FindRenderedFunctionBlockEnd(*lines, cursor);
                    if (function_end >= lines->size() || function_end < cursor) {
                        failed = true;
                        break;
                    }

                    if (entry_count == 0) {
                        replacement_lines.push_back(indent + "return {");
                    }
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  " + rhs_expression);
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = " +
                                                    rhs_expression);
                    }
                    for (std::size_t body_line = cursor + 1; body_line <= function_end;
                         ++body_line) {
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == function_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = function_end + 1;
                } else if (rhs_is_table_literal) {
                    const std::size_t table_end =
                        FindRenderedTableLiteralEnd(*lines, cursor);
                    if (table_end >= lines->size() || table_end < cursor) {
                        failed = true;
                        break;
                    }
                    for (std::size_t body_line = cursor + 1; body_line < table_end; ++body_line) {
                        if (CountIdentifierUses((*lines)[body_line], table_name) != 0) {
                            failed = true;
                            break;
                        }
                    }
                    if (failed) {
                        break;
                    }

                    if (entry_count == 0) {
                        replacement_lines.push_back(indent + "return {");
                    }
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  {");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = {");
                    }
                    for (std::size_t body_line = cursor + 1; body_line <= table_end;
                         ++body_line) {
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == table_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = table_end + 1;
                } else {
                    if (entry_count == 0) {
                        replacement_lines.push_back(indent + "return {");
                    }
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

            if (failed || entry_count == 0 || cursor >= probe_limit ||
                !IsRenderedExactReturnIdentifier((*lines)[cursor], table_name)) {
                continue;
            }

            replacement_lines.push_back(indent + "}");
            std::vector<std::string> merged_lines = prefix_lines;
            merged_lines.insert(merged_lines.end(),
                                replacement_lines.begin(),
                                replacement_lines.end());

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(cursor) + 1);
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
