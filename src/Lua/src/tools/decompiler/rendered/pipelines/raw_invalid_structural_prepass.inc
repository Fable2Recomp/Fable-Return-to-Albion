bool TryNormalizeInvalidNilCallExpressions(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed_any = false;

    const auto is_identifier_char = [](char ch) {
        return std::isalnum(static_cast<unsigned char>(ch)) || ch == '_';
    };

    for (std::string& line : *lines) {
        std::string rewritten;
        rewritten.reserve(line.size());
        bool line_changed = false;
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        std::size_t cursor = 0;
        std::size_t append_from = 0;

        while (cursor < line.size()) {
            const char ch = line[cursor];
            if (escaped) {
                escaped = false;
                ++cursor;
                continue;
            }
            if ((in_single_quote || in_double_quote) && ch == '\\') {
                escaped = true;
                ++cursor;
                continue;
            }
            if (!in_double_quote && ch == '\'') {
                in_single_quote = !in_single_quote;
                ++cursor;
                continue;
            }
            if (!in_single_quote && ch == '"') {
                in_double_quote = !in_double_quote;
                ++cursor;
                continue;
            }
            if (in_single_quote || in_double_quote) {
                ++cursor;
                continue;
            }

            const bool token_match =
                cursor + 4 <= line.size() && line.compare(cursor, 4, "nil(") == 0 &&
                (cursor == 0 || !is_identifier_char(line[cursor - 1]));
            if (!token_match) {
                ++cursor;
                continue;
            }

            std::size_t end = cursor + 3;
            int paren_depth = 0;
            bool found_end = false;
            bool nested_single_quote = false;
            bool nested_double_quote = false;
            bool nested_escaped = false;
            for (; end < line.size(); ++end) {
                const char nested_ch = line[end];
                if (nested_escaped) {
                    nested_escaped = false;
                    continue;
                }
                if ((nested_single_quote || nested_double_quote) && nested_ch == '\\') {
                    nested_escaped = true;
                    continue;
                }
                if (!nested_double_quote && nested_ch == '\'') {
                    nested_single_quote = !nested_single_quote;
                    continue;
                }
                if (!nested_single_quote && nested_ch == '"') {
                    nested_double_quote = !nested_double_quote;
                    continue;
                }
                if (nested_single_quote || nested_double_quote) {
                    continue;
                }
                if (nested_ch == '(') {
                    ++paren_depth;
                } else if (nested_ch == ')') {
                    --paren_depth;
                    if (paren_depth == 0) {
                        found_end = true;
                        break;
                    }
                }
            }

            if (!found_end) {
                ++cursor;
                continue;
            }

            rewritten.append(line, append_from, cursor - append_from);
            rewritten.append("nil");
            append_from = end + 1;
            cursor = end + 1;
            line_changed = true;
        }

        if (line_changed) {
            rewritten.append(line, append_from, std::string::npos);
            line = std::move(rewritten);
            changed_any = true;
        }
    }

    if (!changed_any) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_nil_call_normalized");
    }
    return true;
}

bool TryStripStandaloneEndBeforeTopLevelClassBoundary(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    std::size_t candidate_index = std::string::npos;
    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view current =
            TrimWhitespace(StripLineComment((*lines)[index]));
        if (current != "end") {
            continue;
        }

        std::size_t next = index + 1;
        while (next < lines->size()) {
            const std::string_view next_trimmed =
                TrimWhitespace(StripLineComment((*lines)[next]));
            if (next_trimmed.empty()) {
                ++next;
                continue;
            }
            const bool looks_like_top_level_class_boundary =
                StartsWith(next_trimmed, "BehaviourBase:CreateSubClass(") ||
                next_trimmed.find(":CreateSubClass(") != std::string_view::npos ||
                next_trimmed.find("= BehaviourBase:CreateSubClass(") !=
                    std::string_view::npos ||
                StartsWith(next_trimmed, "Behaviour") ||
                StartsWith(next_trimmed, "Quest") ||
                StartsWith(next_trimmed, "AIManager");
            if (looks_like_top_level_class_boundary) {
                // Strip only the trailing-most standalone boundary `end`.
                candidate_index = index;
            }
            break;
        }
    }

    if (candidate_index == std::string::npos) {
        return false;
    }
    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(candidate_index));
    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_stripped_standalone_end_before_boundary");
    }
    return true;
}

bool TryApplyRawInvalidBoundaryRepairSequence(std::string& source,
                                              const std::string& validation_error,
                                              std::string* note) {
    bool changed = false;
    changed |= TryRepairValidatedTopLevelFunctionBoundaryBleed(source, note);
    changed |=
        TryInsertEndBeforeMalformedTopLevelBoundary(source, validation_error, note);
    changed |= TryBalanceOpenBlocks(source, validation_error, note);
    changed |= TryAppendMissingEnds(source, validation_error, note);
    return changed;
}

bool TryApplyRawInvalidMethodBoundaryRepairSequence(std::string& source,
                                                    const std::string& validation_error,
                                                    std::string* note) {
    bool changed = false;
    changed |= TryApplyMethodLabelStructuralNormalization(source, note);
    changed |=
        TryApplyRawInvalidBoundaryRepairSequence(source, validation_error, note);
    return changed;
}

bool TryApplyRawInvalidInlineTableBoundaryRepairSequence(
    std::string& source,
    const std::string& validation_error,
    std::string* note) {
    bool changed = false;
    changed |= TryRewriteInlineTableLiteralFieldAssignments(source, note);
    changed |=
        TryApplyRawInvalidMethodBoundaryRepairSequence(source, validation_error, note);
    return changed;
}

bool TryNormalizeBrokenBracketMemberIndexExpressions(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed_any = false;

    for (std::string& line : *lines) {
        std::size_t search_from = 0;
        while (search_from < line.size()) {
            const std::size_t start = line.find("[\"", search_from);
            if (start == std::string::npos) {
                break;
            }
            const std::size_t quote_end = line.find('"', start + 2);
            if (quote_end == std::string::npos) {
                break;
            }
            if (quote_end + 2 >= line.size() || line[quote_end + 1] != '.') {
                search_from = quote_end + 1;
                continue;
            }
            const std::size_t ident_start = quote_end + 2;
            std::size_t ident_end = ident_start;
            while (ident_end < line.size()) {
                const char ch = line[ident_end];
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                    (ch >= '0' && ch <= '9') || ch == '_') {
                    ++ident_end;
                    continue;
                }
                break;
            }
            if (ident_end == ident_start || ident_end >= line.size() ||
                line[ident_end] != ']') {
                search_from = quote_end + 1;
                continue;
            }

            const std::string identifier = line.substr(ident_start, ident_end - ident_start);
            if (!refii::lua::IsValidLuaIdentifier(identifier) ||
                refii::lua::IsLuaKeyword(identifier)) {
                search_from = ident_end + 1;
                continue;
            }

            const std::string replacement = "[ScriptEnum." + identifier + "]";
            line.replace(start, ident_end - start + 1, replacement);
            search_from = start + replacement.size();
            changed_any = true;
        }
    }

    if (!changed_any) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_bracket_member_index_normalized");
    }
    return true;
}

bool TryNormalizeInvalidNumericCallPrefixes(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed_any = false;

    for (std::string& line : *lines) {
        std::string rewritten;
        rewritten.reserve(line.size());
        bool line_changed = false;
        for (std::size_t i = 0; i < line.size(); ++i) {
            const char ch = line[i];
            if (ch >= '0' && ch <= '9') {
                std::size_t digit_end = i;
                while (digit_end < line.size() && line[digit_end] >= '0' &&
                       line[digit_end] <= '9') {
                    ++digit_end;
                }
                if (digit_end < line.size() && line[digit_end] == '(') {
                    const bool at_identifier_tail =
                        i > 0 &&
                        (std::isalnum(static_cast<unsigned char>(line[i - 1])) != 0 ||
                         line[i - 1] == '_');
                    if (!at_identifier_tail) {
                        line_changed = true;
                        i = digit_end - 1;
                        continue;
                    }
                }
            }
            rewritten.push_back(ch);
        }
        if (line_changed) {
            line = std::move(rewritten);
            changed_any = true;
        }
    }

    if (!changed_any) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_numeric_call_prefix_normalized");
    }
    return true;
}

bool TryNormalizeInvalidParenthesizedTupleExpressions(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed_any = false;

    for (std::string& line : *lines) {
        std::size_t search_from = 0;
        while (search_from < line.size()) {
            const std::size_t open = line.find("((", search_from);
            if (open == std::string::npos) {
                break;
            }
            const std::size_t close = line.find("))", open + 2);
            if (close == std::string::npos) {
                break;
            }
            const std::string_view inner(line.data() + open + 2, close - (open + 2));
            const std::size_t comma = inner.find(',');
            if (comma == std::string_view::npos) {
                search_from = close + 2;
                continue;
            }
            const std::string_view first_value = TrimWhitespace(inner.substr(0, comma));
            if (first_value.empty()) {
                search_from = close + 2;
                continue;
            }
            const std::string replacement = "((" + std::string(first_value) + "))";
            line.replace(open, close - open + 2, replacement);
            search_from = open + replacement.size();
            changed_any = true;
        }
    }

    if (!changed_any) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_parenthesized_tuple_normalized");
    }
    return true;
}

bool TryBalanceTrailingParenthesesInAssignments(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed_any = false;

    const auto line_paren_delta = [](std::string_view text) {
        int delta = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        for (char ch : text) {
            if (escaped) {
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
                ++delta;
            } else if (ch == ')') {
                --delta;
            }
        }
        return delta;
    };

    for (std::size_t i = 0; i < lines->size(); ++i) {
        const std::string_view stripped = StripLineComment((*lines)[i]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty() || trimmed.find('=') == std::string_view::npos) {
            continue;
        }
        const int delta = line_paren_delta(stripped);
        if (delta <= 0) {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines->size()) {
            const std::string_view next_trimmed =
                TrimWhitespace(StripLineComment((*lines)[next]));
            if (next_trimmed.empty()) {
                ++next;
                continue;
            }
            const bool looks_like_next_statement =
                next_trimmed.find('=') != std::string_view::npos ||
                StartsWith(next_trimmed, "if ") ||
                StartsWith(next_trimmed, "while ") ||
                StartsWith(next_trimmed, "for ") ||
                next_trimmed == "end";
            if (!looks_like_next_statement) {
                break;
            }
            (*lines)[i].append(static_cast<std::size_t>(delta), ')');
            changed_any = true;
            break;
        }
    }

    if (!changed_any) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_assignment_paren_balance_normalized");
    }
    return true;
}

bool TryNormalizeOrphanTopLevelTableListAssignment(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed = false;

    const auto has_l1_assignment_use = [&]() {
        for (const std::string& line : *lines) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (trimmed.find("= L1_1") != std::string_view::npos) {
                return true;
            }
        }
        return false;
    };

    if (!has_l1_assignment_use()) {
        return false;
    }

    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment((*lines)[index]));
        if (trimmed != "{") {
            continue;
        }
        const std::size_t indent_end = (*lines)[index].find_first_not_of(" \t");
        const std::string indent = indent_end == std::string::npos
                                       ? std::string()
                                       : (*lines)[index].substr(0, indent_end);
        (*lines)[index] = indent + "local L1_1 = {{";
        changed = true;
        break;
    }

    if (!changed) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_orphan_table_list_normalized");
    }
    return true;
}

bool TryNormalizeMalformedCallResultAssignments(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed_any = false;
    const auto find_top_level_comma = [](std::string_view value) -> std::size_t {
        int paren_depth = 0;
        int bracket_depth = 0;
        int brace_depth = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        for (std::size_t i = 0; i < value.size(); ++i) {
            const char ch = value[i];
            if (escaped) {
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
            switch (ch) {
            case '(':
                ++paren_depth;
                break;
            case ')':
                if (paren_depth > 0) {
                    --paren_depth;
                }
                break;
            case '[':
                ++bracket_depth;
                break;
            case ']':
                if (bracket_depth > 0) {
                    --bracket_depth;
                }
                break;
            case '{':
                ++brace_depth;
                break;
            case '}':
                if (brace_depth > 0) {
                    --brace_depth;
                }
                break;
            case ',':
                if (paren_depth == 0 && bracket_depth == 0 && brace_depth == 0) {
                    return i;
                }
                break;
            default:
                break;
            }
        }
        return std::string_view::npos;
    };

    for (std::string& line : *lines) {
        const std::string_view stripped = StripLineComment(line);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        const std::size_t equals_pos = trimmed.find('=');
        if (equals_pos == std::string::npos) {
            continue;
        }

        const std::string_view lhs = TrimWhitespace(trimmed.substr(0, equals_pos));
        const std::string_view rhs = trimmed.substr(equals_pos + 1);
        const std::size_t comma_pos = find_top_level_comma(lhs);
        if (comma_pos == std::string::npos) {
            continue;
        }

        const std::string_view first_lhs = TrimWhitespace(lhs.substr(0, comma_pos));
        const std::string_view second_lhs = TrimWhitespace(lhs.substr(comma_pos + 1));
        if (first_lhs.empty() || second_lhs.empty()) {
            continue;
        }

        if (first_lhs.find('(') == std::string_view::npos ||
            first_lhs.find(')') == std::string_view::npos) {
            continue;
        }

        if (!IsSimpleLuaIdentityToken(second_lhs)) {
            continue;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent = indent_end == std::string::npos
                                       ? std::string()
                                       : line.substr(0, indent_end);
        line = indent + "_, " + std::string(second_lhs) + " =" + std::string(rhs);
        changed_any = true;
    }

    if (!changed_any) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_malformed_call_result_assignment_normalized");
    }
    return true;
}

bool TryApplyTargetedRawInvalidStructuralPreRepair(std::string& source,
                                                   std::string* validation_error,
                                                   std::string_view validation_label,
                                                   std::string* note) {
    if (validation_error == nullptr || validation_error->empty() ||
        !IsTargetedRawInvalidStructuralFamily(*validation_error)) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext text_context(source);
    const auto validate_source = [&]() {
        text_context.SetSourceView(source);
        return text_context.ValidateCached(validation_label);
    };

    bool changed_any = false;
    const int targeted_prerepair_pass_cap = GetCurrentRepairAttemptCap(2, 1);
    RunBoundedNormalizationPassLoop(targeted_prerepair_pass_cap, [&](int /*pass*/) {
        if (validation_error->empty()) {
            return false;
        }
        bool changed = false;
        changed |= TryStripSyntheticTopLevelFunctionWrapper(source, note);

        if (validation_error->find("unexpected symbol near 'while'") !=
            std::string::npos) {
            changed |= TryRewriteRepeatIfYieldElseContinuations(source, note);
            changed |= TryRewriteRepeatStateLadderLoops(source, note);
            changed |= TryRewriteRepeatLeadingStateLoops(source, note);
        }

        if (validation_error->find("unexpected symbol near 'if'") !=
            std::string::npos) {
            changed |=
                TryApplyRawInvalidBoundaryRepairSequence(source, *validation_error, note);
        }

        if (validation_error->find("unexpected symbol near 'end'") !=
            std::string::npos) {
            changed |=
                TryApplyRawInvalidBoundaryRepairSequence(source, *validation_error, note);
        }

        if (validation_error->find("unexpected symbol near '='") !=
            std::string::npos) {
            changed |= TryApplyRawInvalidInlineTableBoundaryRepairSequence(
                source, *validation_error, note);
        }

        if (validation_error->find("unexpected symbol near '{'") !=
            std::string::npos) {
            changed |= TryNormalizeOrphanTopLevelTableListAssignment(source, note);
            changed |= TryApplyRawInvalidInlineTableBoundaryRepairSequence(
                source, *validation_error, note);
        }

        if (validation_error->find("unexpected symbol near 'nil'") !=
            std::string::npos) {
            changed |= TryNormalizeInvalidNilCallExpressions(source, note);
            changed |= TryApplyRawInvalidMethodBoundaryRepairSequence(
                source, *validation_error, note);
        }

        if (validation_error->find("')' expected near ':'") != std::string::npos) {
            changed |= TryApplyMethodLabelStructuralNormalization(source, note);
        }

        if (validation_error->find("')' expected near '('") != std::string::npos) {
            changed |= TryStripSyntheticTopLevelFunctionWrapper(source, note);
            changed |= TryNormalizeInvalidNilCallExpressions(source, note);
            changed |= TryNormalizeInvalidNumericCallPrefixes(source, note);
            changed |= TryNormalizeMalformedCallResultAssignments(source, note);
            changed |=
                TryApplyRawInvalidBoundaryRepairSequence(source, *validation_error, note);
        }

        if (validation_error->find("unexpected symbol near ','") !=
            std::string::npos) {
            changed |= TryNormalizeMalformedCallResultAssignments(source, note);
        }

        if (validation_error->find("'}' expected (to close '{'") != std::string::npos) {
            changed |= TryRepairInlineTableFunctionFieldBoundaryBreakage(source, note);
            changed |= TryRepairInlineTableFunctionPrematureEndBreakage(source, note);
            changed |=
                TryApplyRawInvalidBoundaryRepairSequence(source, *validation_error, note);
        }

        if (validation_error->find("unexpected symbol near ')'") != std::string::npos) {
            changed |= TryApplyRawInvalidMethodBoundaryRepairSequence(
                source, *validation_error, note);
        }

        if (validation_error->find("unexpected symbol near '('") !=
            std::string::npos) {
            changed |= TryNormalizeInvalidNumericCallPrefixes(source, note);
            changed |= TryApplyRawInvalidMethodBoundaryRepairSequence(
                source, *validation_error, note);
        }

        if (validation_error->find("')' expected near ','") != std::string::npos) {
            changed |= TryNormalizeInvalidParenthesizedTupleExpressions(source, note);
            changed |= TryBalanceTrailingParenthesesInAssignments(source, note);
        }

        if (validation_error->find("')' expected (to close '(' at line") !=
            std::string::npos) {
            changed |= TryBalanceTrailingParenthesesInAssignments(source, note);
        }

        if (validation_error->find("']' expected near '.'") != std::string::npos) {
            changed |= TryNormalizeBrokenBracketMemberIndexExpressions(source, note);
        }

        if (validation_error->find("unexpected symbol near '}'") !=
            std::string::npos) {
            changed |= TryRepairInlineTableFunctionFieldBoundaryBreakage(source, note);
            changed |= TryRepairInlineTableFunctionPrematureEndBreakage(source, note);
            changed |=
                TryApplyRawInvalidBoundaryRepairSequence(source, *validation_error, note);
        }

        if (validation_error->find("'=' expected near") != std::string::npos) {
            changed |= TryApplyRawInvalidMethodBoundaryRepairSequence(
                source, *validation_error, note);
        }

        if (validation_error->find("<eof> expected near") != std::string::npos ||
            validation_error->find("'<eof>' expected near") != std::string::npos) {
            changed |= TryStripStandaloneEndBeforeTopLevelClassBoundary(source, note);
            changed |=
                TryApplyRawInvalidBoundaryRepairSequence(source, *validation_error, note);
        }

        if (!changed) {
            return false;
        }

        changed_any = true;
        *validation_error = validate_source();
        if (note != nullptr) {
            if (validation_error->empty()) {
                *note = JoinNotes(*note, "raw_invalid_structural_prepass_validated");
            } else {
                *note = JoinNotes(*note, "raw_invalid_structural_prepass_progress");
            }
        }
        return true;
    });

    return changed_any;
}
