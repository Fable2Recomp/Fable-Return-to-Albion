bool IsLargeChunkSyntaxRepairCandidate(std::string_view validation_error) {
    if (validation_error.empty()) {
        return false;
    }

    return validation_error.find("<eof>") != std::string_view::npos ||
           validation_error.find("'end' expected") != std::string_view::npos ||
           validation_error.find("near 'else'") != std::string_view::npos ||
           validation_error.find("near 'elseif'") != std::string_view::npos ||
           validation_error.find("near 'until'") != std::string_view::npos ||
           validation_error.find("near 'return'") != std::string_view::npos;
}

bool TryRewriteRepeatStateLadderLoops(std::string& decompiled_source,
                                      std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimWhitespace(refii::lua::StripLuaLineComment(lines[line_index])) != "repeat") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";
        const std::size_t if_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string ignored_condition;
        if (if_line >= lines.size() || GetLuaLineIndent(lines[if_line]) != body_indent ||
            !ParseLuaIfCondition(lines[if_line], &ignored_condition)) {
            continue;
        }

        std::size_t yield_line = lines.size();
        for (std::size_t probe = if_line + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[probe]));
            if (trimmed.empty() || GetLuaLineIndent(lines[probe]) != body_indent) {
                continue;
            }
            if (IsLuaYieldWaitLine(lines[probe])) {
                yield_line = probe;
                break;
            }
            if (trimmed == "end" || trimmed == "else" || StartsWith(trimmed, "elseif ") ||
                StartsWith(trimmed, "while ") || StartsWith(trimmed, "until ")) {
                continue;
            }
            break;
        }
        if (yield_line >= lines.size()) {
            continue;
        }

        const std::size_t if_end =
            FindLastNonEmptyLuaLineInRange(lines, if_line + 1, yield_line);
        if (if_end >= yield_line) {
            continue;
        }

        const std::size_t sibling_loop_start = FindNextNonEmptyLuaLine(lines, yield_line + 1);
        if (sibling_loop_start >= lines.size() ||
            GetLuaLineIndent(lines[sibling_loop_start]) != body_indent) {
            continue;
        }

        const std::string_view sibling_trimmed =
            TrimWhitespace(refii::lua::StripLuaLineComment(lines[sibling_loop_start]));
        if (!StartsWith(sibling_trimmed, "while ") || !EndsWith(sibling_trimmed, " do")) {
            continue;
        }

        const std::size_t sibling_loop_end =
            FindBalancedLuaBlockEnd(lines, sibling_loop_start);
        if (sibling_loop_end >= lines.size()) {
            continue;
        }

        std::size_t close_line = lines.size();
        bool saw_until = false;
        for (std::size_t probe = sibling_loop_end + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[probe]));
            if (trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < outer_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]) != outer_indent) {
                continue;
            }
            if (StartsWith(trimmed, "until ")) {
                saw_until = true;
                break;
            }
            if (trimmed == "end") {
                close_line = probe;
                break;
            }
        }
        if (saw_until || close_line >= lines.size()) {
            continue;
        }

        std::size_t first_branch_boundary = lines.size();
        bool saw_state_branch = false;
        for (std::size_t probe = if_line + 1; probe < if_end; ++probe) {
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[probe]));
            if (GetLuaLineIndent(lines[probe]) == body_indent &&
                (trimmed == "else" || StartsWith(trimmed, "elseif "))) {
                first_branch_boundary = probe;
                saw_state_branch = trimmed.find("CurrentState") != std::string_view::npos;
                break;
            }
        }
        if (first_branch_boundary >= if_end) {
            continue;
        }

        const std::string_view first_if_trimmed =
            TrimWhitespace(refii::lua::StripLuaLineComment(lines[if_line]));
        const bool first_branch_is_state_condition =
            first_if_trimmed.find("CurrentState") != std::string_view::npos;
        const std::size_t first_branch_last =
            FindLastNonEmptyLuaLineInRange(lines, if_line + 1, first_branch_boundary);
        bool first_branch_has_wait = false;
        for (std::size_t probe = if_line + 1; probe < first_branch_boundary; ++probe) {
            if (IsLuaYieldWaitLine(lines[probe])) {
                first_branch_has_wait = true;
                break;
            }
        }
        const bool first_branch_already_terminal =
            first_branch_last < first_branch_boundary &&
            (TrimWhitespace(refii::lua::StripLuaLineComment(lines[first_branch_last])) == "break" ||
             StartsWith(TrimWhitespace(refii::lua::StripLuaLineComment(lines[first_branch_last])),
                        "return "));
        const bool should_insert_break =
            !first_branch_is_state_condition && saw_state_branch && !first_branch_has_wait &&
            !first_branch_already_terminal;

        std::vector<std::string> replacement;
        replacement.reserve(close_line - line_index + 4);
        replacement.push_back(outer_indent + "while true do");
        AppendReindentedLuaBlock(lines,
                                 if_line,
                                 yield_line + 1,
                                 body_indent,
                                 body_indent,
                                 &replacement,
                                 true);
        if (should_insert_break) {
            const std::string branch_body_indent = body_indent + "  ";
            auto insert_it = replacement.end();
            for (auto it = replacement.begin() + 1; it != replacement.end(); ++it) {
                const std::string_view trimmed =
                    TrimWhitespace(refii::lua::StripLuaLineComment(*it));
                if (GetLuaLineIndent(*it) == body_indent &&
                    (trimmed == "else" || StartsWith(trimmed, "elseif "))) {
                    insert_it = it;
                    break;
                }
            }
            replacement.insert(insert_it, branch_body_indent + "break");
        }
        replacement.push_back(outer_indent + "end");
        AppendReindentedLuaBlock(lines,
                                 sibling_loop_start,
                                 sibling_loop_end + 1,
                                 body_indent,
                                 outer_indent,
                                 &replacement,
                                 true);

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(close_line + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rewrote_repeat_state_ladder");
    }
    return true;
}

bool TryRewriteRepeatLeadingStateLoops(std::string& decompiled_source,
                                       std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimWhitespace(refii::lua::StripLuaLineComment(lines[line_index])) != "repeat") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        const std::size_t if_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string condition;
        if (if_line >= lines.size() || GetLuaLineIndent(lines[if_line]) != body_indent ||
            !ParseLuaIfCondition(lines[if_line], &condition) ||
            condition.find("CurrentState") == std::string::npos) {
            continue;
        }

        std::size_t first_boundary = lines.size();
        for (std::size_t probe = if_line + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < outer_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]).size() <= outer_indent.size() &&
                StartsWith(trimmed, "elseif ")) {
                first_boundary = probe;
                break;
            }
        }
        if (first_boundary >= lines.size()) {
            continue;
        }

        const std::size_t first_body_start = FindNextNonEmptyLuaLine(lines, if_line + 1);
        if (first_body_start >= first_boundary) {
            continue;
        }

        bool first_body_has_wait = false;
        for (std::size_t probe = first_body_start; probe < first_boundary; ++probe) {
            if (IsLuaYieldWaitLine(lines[probe])) {
                first_body_has_wait = true;
                break;
            }
        }
        if (!first_body_has_wait) {
            continue;
        }

        std::size_t remainder_end = lines.size();
        for (std::size_t probe = first_boundary + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[probe]));
            if (trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < outer_indent.size()) {
                remainder_end = probe;
                break;
            }
        }
        if (remainder_end <= first_boundary + 1) {
            continue;
        }

        const std::string first_body_indent = GetLuaLineIndent(lines[first_body_start]);
        if (first_body_indent.size() <= outer_indent.size()) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.push_back(outer_indent + "while " + condition + " do");
        AppendReindentedLuaBlock(lines,
                                 first_body_start,
                                 first_boundary,
                                 first_body_indent,
                                 outer_indent + "  ",
                                 &replacement,
                                 true);
        replacement.push_back(outer_indent + "end");
        AppendReindentedLuaBlock(lines,
                                 first_boundary,
                                 remainder_end,
                                 outer_indent,
                                 outer_indent,
                                 &replacement,
                                 true);
        if (replacement.size() <= 3) {
            continue;
        }

        if (StartsWith(TrimWhitespace(refii::lua::StripLuaLineComment(replacement[3])),
                       "elseif ")) {
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(replacement[3]));
            replacement[3] = outer_indent + "if" + std::string(trimmed.substr(6));
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(remainder_end));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rewrote_repeat_leading_state_loop");
    }
    return true;
}

bool TryRewriteRepeatStateMachineLoops(std::string& decompiled_source,
                                       std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimWhitespace(refii::lua::StripLuaLineComment(lines[line_index])) != "repeat") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";
        const std::size_t first_body_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        if (first_body_line >= lines.size() ||
            GetLuaLineIndent(lines[first_body_line]).size() <= outer_indent.size()) {
            continue;
        }

        std::size_t close_line = lines.size();
        bool saw_until = false;
        bool saw_state_branch = false;
        bool saw_wait = false;
        for (std::size_t probe = first_body_line; probe < lines.size(); ++probe) {
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[probe]));
            if (trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < outer_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]) == body_indent &&
                (StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif "))) {
                if (trimmed.find("CurrentState") != std::string_view::npos) {
                    saw_state_branch = true;
                }
            }
            if (IsLuaYieldWaitLine(lines[probe])) {
                saw_wait = true;
            }
            if (GetLuaLineIndent(lines[probe]) != outer_indent) {
                continue;
            }
            if (StartsWith(trimmed, "until ")) {
                saw_until = true;
                break;
            }
            if (trimmed == "end") {
                close_line = probe;
                break;
            }
        }
        if (saw_until || close_line >= lines.size() || !saw_state_branch || !saw_wait) {
            continue;
        }

        const std::size_t indent_end = lines[line_index].find_first_not_of(" \t");
        if (indent_end == std::string::npos) {
            lines[line_index] = "while true do";
        } else {
            lines[line_index].replace(indent_end, std::string::npos, "while true do");
        }
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rewrote_repeat_state_machine_loop");
    }
    return true;
}

bool TryRewriteRepeatMonitorLoopsWithoutUntil(std::string& decompiled_source,
                                              std::string* note) {
    if (decompiled_source.find("repeat") == std::string_view::npos ||
        decompiled_source.find("missing end command somewhere! Added here") ==
            std::string::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimWhitespace(refii::lua::StripLuaLineComment(lines[line_index])) != "repeat") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        std::size_t close_line = lines.size();
        bool saw_until = false;
        bool saw_warning = false;
        bool saw_helper_if = false;
        bool saw_nested_while = false;
        bool saw_yield = false;
        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            const std::string_view raw_trimmed = TrimWhitespace(lines[probe]);
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < outer_indent.size()) {
                break;
            }
            if (lines[probe].find("missing end command somewhere! Added here") !=
                std::string::npos) {
                saw_warning = true;
            }
            if (GetLuaLineIndent(lines[probe]) == body_indent &&
                StartsWith(trimmed, "if ")) {
                saw_helper_if = true;
            }
            if (StartsWith(trimmed, "while ") && EndsWith(trimmed, " do") &&
                GetLuaLineIndent(lines[probe]).size() > body_indent.size()) {
                saw_nested_while = true;
            }
            if (trimmed == "coroutine.yield()") {
                saw_yield = true;
            }
            if (GetLuaLineIndent(lines[probe]) != outer_indent) {
                continue;
            }
            if (StartsWith(trimmed, "until ")) {
                saw_until = true;
                break;
            }
            if (raw_trimmed == "end") {
                close_line = probe;
                break;
            }
        }

        if (saw_until || close_line >= lines.size() || !saw_warning || !saw_helper_if ||
            !saw_nested_while || !saw_yield) {
            continue;
        }

        const std::size_t indent_end = lines[line_index].find_first_not_of(" \t");
        if (indent_end == std::string::npos) {
            lines[line_index] = "while true do";
        } else {
            lines[line_index].replace(indent_end, std::string::npos, "while true do");
        }

        for (std::size_t probe = line_index + 1; probe < close_line;) {
            if (lines[probe].find("missing end command somewhere! Added here") ==
                std::string::npos) {
                ++probe;
                continue;
            }
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(probe));
            --close_line;
        }

        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rewrote_repeat_monitor_loop");
    }
    return true;
}

bool TryLiftStateChainAfterDoReturnEnd(std::string& decompiled_source,
                                       std::string* note) {
    if (decompiled_source.find("do return end") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    auto indentation_of = [&](std::size_t index) -> std::string {
        const std::size_t indent_end = lines[index].find_first_not_of(" \t");
        return indent_end == std::string::npos ? std::string() : lines[index].substr(0, indent_end);
    };
    const auto previous_significant = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t probe = start; probe-- > 0;) {
            const std::string_view trimmed = TrimWhitespace(lines[probe]);
            if (!trimmed.empty() && !StartsWith(trimmed, "--")) {
                return probe;
            }
            if (probe == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto dedent_once = [](std::string indent) -> std::string {
        if (indent.empty()) {
            return indent;
        }
        if (indent.back() == '\t') {
            indent.pop_back();
            return indent;
        }
        if (indent.size() >= 2 && indent.substr(indent.size() - 2) == "  ") {
            indent.resize(indent.size() - 2);
            return indent;
        }
        indent.pop_back();
        return indent;
    };
    const auto parent_branch_indent_of = [&](std::size_t index) -> std::string {
        const std::string indent = indentation_of(index);
        if (indent.empty()) {
            return indent;
        }

        for (std::size_t probe = index; probe-- > 0;) {
            const std::string_view trimmed = TrimWhitespace(lines[probe]);
            if (trimmed.empty() || StartsWith(trimmed, "--")) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string probe_indent = indentation_of(probe);
            if (probe_indent.size() >= indent.size()) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ")) {
                return probe_indent;
            }
        }

        return dedent_once(indent);
    };
    const auto strip_duplicate_nested_end_before_owner_elseif =
        [&](std::size_t continuation_index, const std::string& owner_indent) -> bool {
            if (continuation_index >= lines.size()) {
                return false;
            }

            const std::string_view continuation_trimmed = TrimWhitespace(lines[continuation_index]);
            if ((!StartsWith(continuation_trimmed, "elseif ") &&
                 !StartsWith(continuation_trimmed, "if ")) ||
                continuation_trimmed.find("CurrentState") == std::string_view::npos ||
                indentation_of(continuation_index) != owner_indent) {
                return false;
            }

            const std::size_t duplicate_end = previous_significant(continuation_index);
            if (duplicate_end >= lines.size() || TrimWhitespace(lines[duplicate_end]) != "end") {
                return false;
            }

            const std::size_t previous_end = previous_significant(duplicate_end);
            if (previous_end >= lines.size() || TrimWhitespace(lines[previous_end]) != "end") {
                return false;
            }

            const std::string end_indent = indentation_of(duplicate_end);
            if (end_indent.empty() || indentation_of(previous_end) != end_indent ||
                end_indent.size() <= owner_indent.size()) {
                return false;
            }

            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(duplicate_end));
            return true;
        };

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "do return end") {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (!StartsWith(next_trimmed, "if ") || !EndsWith(next_trimmed, " then") ||
            next_trimmed.find("CurrentState") == std::string_view::npos) {
            continue;
        }

        const std::size_t previous = previous_significant(i);
        const std::string_view previous_trimmed =
            previous < lines.size() ? TrimWhitespace(lines[previous]) : std::string_view();
        const bool previous_closes_branch =
            previous < lines.size() &&
            (previous_trimmed == "end" || previous_trimmed == "break" ||
             previous_trimmed == "return" || StartsWith(previous_trimmed, "return "));
        if (previous_closes_branch) {
            continue;
        }

        const std::string current_indent = indentation_of(i);
        const std::string branch_indent = parent_branch_indent_of(i);
        lines[i] = current_indent + "end";
        lines[next] = branch_indent + "elseif " + std::string(next_trimmed.substr(3));

        std::size_t lift_end = lines.size();
        for (std::size_t probe = next + 1; probe < lines.size(); ++probe) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                continue;
            }
            if (indentation_of(probe).size() <= branch_indent.size()) {
                lift_end = probe;
                break;
            }
        }
        for (std::size_t probe = next + 1; probe < lift_end; ++probe) {
            const std::string current_indent = indentation_of(probe);
            if (current_indent.size() <= branch_indent.size()) {
                continue;
            }
            const std::string lifted_indent = dedent_once(current_indent);
            lines[probe] = lifted_indent + lines[probe].substr(current_indent.size());
        }
        changed |= strip_duplicate_nested_end_before_owner_elseif(next, branch_indent);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_lifted_state_chain_after_do_return");
    }
    return true;
}

bool TryLiftStateContinuationAfterWaitLoop(std::string& decompiled_source,
                                           std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    const auto dedent_once = [](std::string indent) -> std::string {
        if (indent.empty()) {
            return indent;
        }
        if (indent.back() == '\t') {
            indent.pop_back();
            return indent;
        }
        if (indent.size() >= 2 && indent.substr(indent.size() - 2) == "  ") {
            indent.resize(indent.size() - 2);
            return indent;
        }
        indent.pop_back();
        return indent;
    };
    const auto indentation_of = [&](std::size_t index) -> std::string {
        const std::size_t indent_end = lines[index].find_first_not_of(" \t");
        return indent_end == std::string::npos ? std::string() : lines[index].substr(0, indent_end);
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string condition;
        if (!ParseLuaWhileCondition(lines[line_index], &condition) ||
            condition.find("CurrentState") == std::string::npos ||
            condition == "true") {
            continue;
        }

        const std::size_t end_line = FindBalancedLuaBlockEnd(lines, line_index);
        if (end_line >= lines.size()) {
            continue;
        }

        std::size_t next = end_line + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size()) {
            continue;
        }

        const std::string while_indent = indentation_of(line_index);
        const std::string parent_indent = dedent_once(while_indent);
        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (indentation_of(next) != while_indent ||
            ((!StartsWith(next_trimmed, "elseif ") && !StartsWith(next_trimmed, "if ")) ||
             next_trimmed.find("CurrentState") == std::string_view::npos)) {
            continue;
        }

        lines[next] = parent_indent +
                      (StartsWith(next_trimmed, "if ")
                           ? "elseif " + std::string(next_trimmed.substr(3))
                           : std::string(next_trimmed));

        std::size_t lift_end = lines.size();
        for (std::size_t probe = next + 1; probe < lines.size(); ++probe) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                continue;
            }
            if (indentation_of(probe).size() <= parent_indent.size()) {
                lift_end = probe;
                break;
            }
        }
        for (std::size_t probe = next + 1; probe < lift_end; ++probe) {
            const std::string current_indent = indentation_of(probe);
            if (current_indent.size() <= parent_indent.size()) {
                continue;
            }
            const std::string lifted_indent = dedent_once(current_indent);
            lines[probe] = lifted_indent + lines[probe].substr(current_indent.size());
        }
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_lifted_state_continuation_after_wait_loop");
    }
    return true;
}
