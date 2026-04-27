bool SimplifyRenderedLuaIndexedAliveCleanupLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_indexed_loop_condition = [](std::string_view condition,
                                                 std::string* index_name,
                                                 std::string* table_expression) -> bool {
        if (index_name == nullptr || table_expression == nullptr) {
            return false;
        }

        condition = TrimLuaWhitespace(condition);
        const std::size_t le_pos = condition.find("<=");
        if (le_pos == std::string_view::npos) {
            return false;
        }

        const std::string index =
            StripRenderedWholeParentheses(TrimLuaWhitespace(condition.substr(0, le_pos)), nullptr);
        std::string_view rhs = TrimLuaWhitespace(condition.substr(le_pos + 2));
        if (!IsValidLuaIdentifier(index) || !rhs.starts_with('#')) {
            return false;
        }

        const std::string table_expr =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs.substr(1)), nullptr);
        if (!IsRenderedSafePrefixExpression(table_expr)) {
            return false;
        }

        *index_name = index;
        *table_expression = table_expr;
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 9 < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index], &outer_condition)) {
            continue;
        }

        std::string index_name;
        std::string table_expression;
        if (!parse_indexed_loop_condition(outer_condition, &index_name, &table_expression)) {
            continue;
        }

        std::string repeated_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index + 1], &repeated_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(repeated_condition), nullptr) !=
                StripRenderedWholeParentheses(TrimLuaWhitespace(outer_condition), nullptr)) {
            continue;
        }

        std::string inner_if_condition;
        std::string alive_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index + 2], &inner_if_condition) ||
            !TryExtractRenderedWhileCondition((*lines)[line_index + 3], &alive_condition)) {
            continue;
        }

        const std::string element_expression = table_expression + "[" + index_name + "]";
        if (StripRenderedWholeParentheses(TrimLuaWhitespace(inner_if_condition), nullptr) !=
                element_expression ||
            !alive_condition.starts_with(element_expression + ":IsAlive(") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 5])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 6])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 7])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 8])) != "break" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 9])) != "end") {
            continue;
        }

        bool increment_is_local = false;
        std::string increment_lhs;
        std::string increment_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 4],
                                               &increment_is_local,
                                               &increment_lhs,
                                               &increment_rhs) ||
            increment_is_local ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(increment_lhs), nullptr) != index_name) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = std::string(outer_indent) + "  ";
        const std::string nested_indent = inner_indent + "  ";
        const std::string increment_line =
            nested_indent + std::string(TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4])));

        std::vector<std::string> replacement = {
            std::string(outer_indent) + "while " + outer_condition + " do",
            inner_indent + "if " + element_expression + " and " + alive_condition + " then",
            increment_line,
            inner_indent + "else",
            nested_indent + "table.remove(" + table_expression + ", " + index_name + ")",
            inner_indent + "end",
            std::string(outer_indent) + "end",
        };

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 10));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaLoopGuardedYieldElse(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_negated_identifier = [](std::string_view condition,
                                             std::string* identifier) -> bool {
        if (identifier == nullptr) {
            return false;
        }

        condition = TrimLuaWhitespace(condition);
        if (!StartsWithLuaKeyword(condition, "not")) {
            return false;
        }

        const std::string candidate = StripRenderedWholeParentheses(
            TrimLuaWhitespace(condition.substr(3)), nullptr);
        if (!IsValidLuaIdentifier(candidate)) {
            return false;
        }

        *identifier = candidate;
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string while_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index], &while_condition)) {
            continue;
        }

        std::string guard_name;
        if (!parse_negated_identifier(while_condition, &guard_name)) {
            continue;
        }

        const std::size_t loop_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (loop_end >= lines->size() || loop_end <= line_index + 2) {
            continue;
        }

        const std::size_t yield_line =
            FindRenderedLastNonEmptyLine(*lines, line_index + 1, loop_end);
        if (yield_line >= loop_end ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[yield_line])) != "coroutine.yield()") {
            continue;
        }

        for (std::size_t probe = line_index + 1; probe < yield_line; ++probe) {
            std::string if_condition;
            if (!TryExtractRenderedIfCondition((*lines)[probe], &if_condition)) {
                continue;
            }

            const std::size_t if_end = FindRenderedCleanupBlockEnd(*lines, probe);
            if (if_end >= lines->size() || if_end + 1 != yield_line ||
                RenderedLuaBlockHasTopLevelElse(*lines, probe, if_end)) {
                continue;
            }

            bool assigns_guard_true = false;
            for (std::size_t body = probe + 1; body < if_end; ++body) {
                bool assign_is_local = false;
                std::string assign_lhs;
                std::string assign_rhs;
                if (!ParseRenderedSingleAssignmentLine((*lines)[body],
                                                       &assign_is_local,
                                                       &assign_lhs,
                                                       &assign_rhs) ||
                    assign_is_local) {
                    continue;
                }

                if (StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr) ==
                        guard_name &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr) ==
                        "true") {
                    assigns_guard_true = true;
                    break;
                }
            }
            if (!assigns_guard_true) {
                continue;
            }

            const std::string if_indent(GetRenderedLuaIndent((*lines)[probe]));
            const std::string yield_indent = if_indent + "  ";
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(if_end),
                          if_indent + "else");
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(if_end + 1),
                          yield_indent + "coroutine.yield()");
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(yield_line + 2));
            changed = true;
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaYieldElseWaitLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_top_level_else_line = [](const std::vector<std::string>& local_lines,
                                             std::size_t start_line,
                                             std::size_t end_line) -> std::size_t {
        if (start_line >= local_lines.size() || end_line >= local_lines.size() ||
            end_line <= start_line) {
            return local_lines.size();
        }

        int depth = 1;
        for (std::size_t probe = start_line + 1; probe < end_line; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                return probe;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }

        return local_lines.size();
    };

    const auto find_trailing_yield_line = [](const std::vector<std::string>& local_lines,
                                             std::size_t begin,
                                             std::size_t end) -> std::size_t {
        std::size_t yield_line = local_lines.size();
        for (std::size_t probe = begin; probe < end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
            if (trimmed.empty()) {
                continue;
            }
            yield_line = probe;
        }

        if (yield_line >= local_lines.size() ||
            TrimLuaWhitespace(StripLuaLineComment(local_lines[yield_line])) !=
                "coroutine.yield()") {
            return local_lines.size();
        }
        return yield_line;
    };

    const auto append_reindented_block = [](const std::vector<std::string>& local_lines,
                                            std::size_t begin,
                                            std::size_t end,
                                            std::string_view old_prefix,
                                            std::string_view new_prefix,
                                            std::vector<std::string>* output) {
        if (output == nullptr) {
            return;
        }

        for (std::size_t probe = begin; probe < end; ++probe) {
            std::string moved_line = local_lines[probe];
            if (old_prefix.empty()) {
                output->push_back(std::move(moved_line));
                continue;
            }

            const std::string_view current_indent = GetRenderedLuaIndent(moved_line);
            if (current_indent.starts_with(old_prefix)) {
                std::string rewritten =
                    std::string(new_prefix) + std::string(current_indent.substr(old_prefix.size())) +
                    std::string(moved_line.substr(current_indent.size()));
                output->push_back(std::move(rewritten));
            } else {
                output->push_back(std::move(moved_line));
            }
        }
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string if_condition;
        if (TryExtractRenderedIfCondition((*lines)[line_index], &if_condition)) {
            const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
            if (block_end < lines->size() && block_end > line_index + 2) {
                const std::size_t else_line =
                    find_top_level_else_line(*lines, line_index, block_end);
                if (else_line < block_end) {
                    const std::size_t yield_line =
                        find_trailing_yield_line(*lines, line_index + 1, else_line);
                    const std::size_t body_last =
                        FindRenderedLastNonEmptyLine(*lines, else_line + 1, block_end);
                    if (yield_line < else_line && body_last < block_end) {
                        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
                        const std::string inner_indent = indent + "  ";

                        std::vector<std::string> replacement = {
                            indent + "while " +
                                std::string(TrimLuaWhitespace(if_condition)) + " do"};
                        append_reindented_block(
                            *lines, line_index + 1, else_line, inner_indent, inner_indent, &replacement);
                        replacement.push_back(indent + "end");
                        append_reindented_block(
                            *lines, else_line + 1, block_end, inner_indent, indent, &replacement);

                        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                     lines->begin() +
                                         static_cast<std::ptrdiff_t>(block_end + 1));
                        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                      replacement.begin(),
                                      replacement.end());
                        changed = true;
                        if (line_index != 0) {
                            --line_index;
                        }
                        continue;
                    }
                }
            }
        }

        std::string while_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index], &while_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(while_condition), nullptr) !=
                "true") {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (outer_end >= lines->size() || outer_end <= line_index + 2) {
            continue;
        }

        std::size_t inner_if_line = lines->size();
        for (std::size_t probe = line_index + 1; probe < outer_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (!trimmed.empty()) {
                inner_if_line = probe;
                break;
            }
        }
        if (inner_if_line >= outer_end) {
            continue;
        }

        std::string inner_condition;
        if (!TryExtractRenderedIfCondition((*lines)[inner_if_line], &inner_condition)) {
            continue;
        }

        const std::size_t inner_end = FindRenderedCleanupBlockEnd(*lines, inner_if_line);
        if (inner_end >= lines->size() || inner_end >= outer_end ||
            FindRenderedLastNonEmptyLine(*lines, inner_end + 1, outer_end) != lines->size()) {
            continue;
        }

        const std::size_t else_line =
            find_top_level_else_line(*lines, inner_if_line, inner_end);
        if (else_line >= inner_end) {
            continue;
        }
        const std::size_t yield_line =
            find_trailing_yield_line(*lines, inner_if_line + 1, else_line);
        const std::size_t body_last =
            FindRenderedLastNonEmptyLine(*lines, else_line + 1, inner_end);
        if (yield_line >= else_line || body_last >= inner_end) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = outer_indent + "  ";
        const std::string body_prefix =
            std::string(GetRenderedLuaIndent((*lines)[inner_if_line])) + "  ";

        std::vector<std::string> replacement = {
            outer_indent + "while " + std::string(TrimLuaWhitespace(inner_condition)) + " do",
        };
        append_reindented_block(
            *lines, inner_if_line + 1, else_line, body_prefix, inner_indent, &replacement);
        replacement.push_back(outer_indent + "end");
        append_reindented_block(
            *lines, else_line + 1, inner_end, body_prefix, outer_indent, &replacement);

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(outer_end + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaDanglingOuterTrueWaitWrappers(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_first_nonempty = [](const std::vector<std::string>& local_lines,
                                        std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < local_lines.size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
            if (!trimmed.empty()) {
                return probe;
            }
        }
        return local_lines.size();
    };

    const auto find_same_indent_else = [](const std::vector<std::string>& local_lines,
                                          std::size_t start,
                                          std::string_view indent) -> std::size_t {
        for (std::size_t probe = start; probe < local_lines.size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
            if (trimmed.empty()) {
                continue;
            }

            const std::string_view probe_indent = GetRenderedLuaIndent(local_lines[probe]);
            if (probe_indent.size() < indent.size()) {
                return local_lines.size();
            }
            if (probe_indent != indent) {
                continue;
            }
            if (trimmed == "end") {
                return local_lines.size();
            }
            if (trimmed == "else" ||
                (trimmed.starts_with("elseif ") && trimmed.ends_with(" then"))) {
                return probe;
            }
        }
        return local_lines.size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index], &outer_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(outer_condition), nullptr) !=
                "true") {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::size_t inner_start = find_first_nonempty(*lines, line_index + 1);
        if (inner_start >= lines->size()) {
            continue;
        }

        std::string inner_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[inner_start], &inner_condition)) {
            continue;
        }

        const std::size_t inner_end = FindRenderedCleanupBlockEnd(*lines, inner_start);
        if (inner_end >= lines->size()) {
            continue;
        }

        const std::size_t dangling_else = find_same_indent_else(*lines, inner_end + 1, indent);
        if (dangling_else >= lines->size()) {
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

bool SimplifyRenderedLuaDanglingElseGuardedWaitLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_same_indent_else = [](const std::vector<std::string>& local_lines,
                                          std::size_t start,
                                          std::string_view indent) -> std::size_t {
        for (std::size_t probe = start; probe < local_lines.size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
            if (trimmed.empty()) {
                continue;
            }

            const std::string_view probe_indent = GetRenderedLuaIndent(local_lines[probe]);
            if (probe_indent.size() < indent.size()) {
                return local_lines.size();
            }
            if (probe_indent != indent) {
                continue;
            }
            if (trimmed == "end") {
                return local_lines.size();
            }
            if (trimmed == "else" ||
                (trimmed.starts_with("elseif ") && trimmed.ends_with(" then"))) {
                return probe;
            }
        }
        return local_lines.size();
    };

    const auto last_nonempty = [](const std::vector<std::string>& local_lines,
                                  std::size_t begin,
                                  std::size_t end) -> std::size_t {
        if (begin >= end || end > local_lines.size()) {
            return local_lines.size();
        }

        for (std::size_t probe = end; probe-- > begin;) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
            if (!trimmed.empty()) {
                return probe;
            }
        }
        return local_lines.size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string while_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index], &while_condition)) {
            continue;
        }

        const std::string normalized_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(while_condition), nullptr);
        if (normalized_condition.empty() || normalized_condition == "true") {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::size_t else_line = find_same_indent_else(*lines, line_index + 1, indent);
        if (else_line >= lines->size()) {
            continue;
        }

        std::size_t yield_line = lines->size();
        for (std::size_t probe = line_index + 1; probe < else_line; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed == "coroutine.yield()") {
                yield_line = probe;
            }
        }
        if (yield_line >= else_line) {
            continue;
        }

        const std::size_t continuation_last = last_nonempty(*lines, yield_line + 1, else_line);
        if (continuation_last >= else_line) {
            continue;
        }

        const std::string inner_indent = indent + "  ";
        std::vector<std::string> replacement = {
            indent + "if " + normalized_condition + " then",
            inner_indent + "while " + normalized_condition + " do",
        };

        for (std::size_t probe = line_index + 1; probe <= yield_line; ++probe) {
            replacement.push_back("  " + (*lines)[probe]);
        }
        replacement.push_back(inner_indent + "end");
        for (std::size_t probe = yield_line + 1; probe < else_line; ++probe) {
            replacement.push_back((*lines)[probe]);
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(else_line));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}
