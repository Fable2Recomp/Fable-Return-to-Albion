bool SimplifyRenderedLuaNestedCurrentStateElseIfLadders(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto parse_current_state_condition = [&](std::string_view line,
                                                   std::string* state_lhs,
                                                   std::string* condition) -> bool {
        if (state_lhs == nullptr || condition == nullptr) {
            return false;
        }

        std::string extracted_condition;
        if (!TryExtractRenderedIfCondition(line, &extracted_condition)) {
            return false;
        }

        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(extracted_condition,
                                            &compare_position,
                                            &compare_op) ||
            compare_op != "==") {
            return false;
        }

        const std::string lhs = NormalizeRenderedConditionOperand(
            extracted_condition.substr(0, compare_position), nullptr);
        const std::string rhs = NormalizeRenderedConditionExpression(
            extracted_condition.substr(compare_position + compare_op.size()), nullptr);
        if (lhs.empty() || rhs.empty() || lhs.find("CurrentState") == std::string::npos) {
            return false;
        }

        *state_lhs = lhs;
        *condition = extracted_condition;
        return true;
    };
    const auto next_nonempty_noncomment = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return lines->size();
    };

    bool changed = false;
    RunRenderedLuaCleanupPassLoop(
        "SimplifyRenderedLuaNestedCurrentStateElseIfLadders",
        96,
        [&]() {
            bool local_change = false;
            for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
                std::string outer_state_lhs;
                std::string dummy_condition;
                if (!parse_current_state_condition(
                        (*lines)[line_index], &outer_state_lhs, &dummy_condition)) {
                    continue;
                }

                const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
                std::size_t else_line = lines->size();
                int depth = 1;
                for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                    const std::string_view trimmed = trim_line((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }

                    if (depth == 1 && trimmed == "else" &&
                        GetRenderedLuaIndent((*lines)[probe]) == outer_indent) {
                        else_line = probe;
                        break;
                    }

                    depth += GetRenderedLuaBlockDelta(trimmed);
                    if (depth == 0) {
                        break;
                    }
                }
                if (else_line >= lines->size()) {
                    continue;
                }

                const std::size_t nested_if_line = next_nonempty_noncomment(else_line + 1);
                if (nested_if_line >= lines->size() ||
                    GetRenderedLuaIndent((*lines)[nested_if_line]) != outer_indent) {
                    continue;
                }

                bool has_intermediate_nonempty = false;
                for (std::size_t probe = else_line + 1; probe < nested_if_line; ++probe) {
                    if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                        has_intermediate_nonempty = true;
                        break;
                    }
                }
                if (has_intermediate_nonempty) {
                    continue;
                }

                std::string nested_state_lhs;
                std::string nested_condition;
                if (!parse_current_state_condition(
                        (*lines)[nested_if_line], &nested_state_lhs, &nested_condition) ||
                    nested_state_lhs != outer_state_lhs) {
                    continue;
                }

                const std::size_t nested_end = FindRenderedCleanupBlockEnd(*lines, nested_if_line);
                if (nested_end >= lines->size() || trim_line((*lines)[nested_end]) != "end") {
                    continue;
                }

                bool tail_has_code = false;
                for (std::size_t probe = nested_end + 1; probe < lines->size(); ++probe) {
                    const std::string_view trimmed = trim_line((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }
                    if (GetRenderedLuaIndent((*lines)[probe]).size() < outer_indent.size()) {
                        break;
                    }
                    if (GetRenderedLuaIndent((*lines)[probe]) == outer_indent &&
                        (trimmed == "end" || trimmed == "else" ||
                         (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")))) {
                        break;
                    }
                    tail_has_code = true;
                    break;
                }
                if (tail_has_code) {
                    continue;
                }

                (*lines)[nested_if_line] = outer_indent + "elseif " + nested_condition + " then";
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(else_line));

                std::size_t adjusted_nested_end = nested_end;
                if (adjusted_nested_end > else_line) {
                    --adjusted_nested_end;
                }
                lines->erase(lines->begin() +
                             static_cast<std::ptrdiff_t>(adjusted_nested_end));

                changed = true;
                local_change = true;
                if (line_index != 0) {
                    --line_index;
                }
                break;
            }
            return local_change;
        });

    return changed;
}

bool SimplifyRenderedLuaNestedElseIfChains(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto extract_selector_lhs = [](std::string_view condition, std::string* lhs_out) {
        if (lhs_out == nullptr) {
            return false;
        }

        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(condition), nullptr);
        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op)) {
            return false;
        }

        const std::string lhs = NormalizeRenderedConditionOperand(
            normalized.substr(0, compare_position), nullptr);
        if (lhs.empty()) {
            return false;
        }

        *lhs_out = lhs;
        return true;
    };
    const auto next_nonempty_noncomment = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return lines->size();
    };

    bool changed = false;
    RunRenderedLuaCleanupPassLoop(
        "SimplifyRenderedLuaNestedElseIfChains",
        96,
        [&]() {
            bool local_change = false;
            for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
                std::string outer_condition;
                if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition)) {
                    continue;
                }
                std::string outer_selector_lhs;
                if (!extract_selector_lhs(outer_condition, &outer_selector_lhs)) {
                    continue;
                }

                const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
                std::size_t else_line = lines->size();
                int depth = 1;
                for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                    const std::string_view trimmed = trim_line((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }

                    if (depth == 1 && trimmed == "else" &&
                        GetRenderedLuaIndent((*lines)[probe]) == outer_indent) {
                        else_line = probe;
                        break;
                    }

                    depth += GetRenderedLuaBlockDelta(trimmed);
                    if (depth == 0) {
                        break;
                    }
                }
                if (else_line >= lines->size()) {
                    continue;
                }

                const std::size_t nested_if_line = next_nonempty_noncomment(else_line + 1);
                if (nested_if_line >= lines->size() ||
                    GetRenderedLuaIndent((*lines)[nested_if_line]) != outer_indent) {
                    continue;
                }

                bool has_intermediate_nonempty = false;
                for (std::size_t probe = else_line + 1; probe < nested_if_line; ++probe) {
                    if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                        has_intermediate_nonempty = true;
                        break;
                    }
                }
                if (has_intermediate_nonempty) {
                    continue;
                }

                std::string nested_condition;
                if (!TryExtractRenderedIfCondition((*lines)[nested_if_line], &nested_condition)) {
                    continue;
                }
                std::string nested_selector_lhs;
                if (!extract_selector_lhs(nested_condition, &nested_selector_lhs) ||
                    nested_selector_lhs != outer_selector_lhs) {
                    continue;
                }

                const std::size_t nested_end = FindRenderedCleanupBlockEnd(*lines, nested_if_line);
                if (nested_end >= lines->size() || trim_line((*lines)[nested_end]) != "end") {
                    continue;
                }

                bool tail_has_code = false;
                for (std::size_t probe = nested_end + 1; probe < lines->size(); ++probe) {
                    const std::string_view trimmed = trim_line((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }
                    if (GetRenderedLuaIndent((*lines)[probe]).size() < outer_indent.size()) {
                        break;
                    }
                    if (GetRenderedLuaIndent((*lines)[probe]) == outer_indent &&
                        (trimmed == "end" || trimmed == "else" ||
                         (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")))) {
                        break;
                    }
                    tail_has_code = true;
                    break;
                }
                if (tail_has_code) {
                    continue;
                }

                (*lines)[nested_if_line] = outer_indent + "elseif " + nested_condition + " then";
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(else_line));

                std::size_t adjusted_nested_end = nested_end;
                if (adjusted_nested_end > else_line) {
                    --adjusted_nested_end;
                }
                lines->erase(lines->begin() +
                             static_cast<std::ptrdiff_t>(adjusted_nested_end));

                changed = true;
                local_change = true;
                if (line_index != 0) {
                    --line_index;
                }
                break;
            }
            return local_change;
        });

    return changed;
}

bool SimplifyRenderedLuaNestedElseIfChainsGeneric(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_noncomment = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return lines->size();
    };

    bool changed = false;
    RunRenderedLuaCleanupPassLoop(
        "SimplifyRenderedLuaNestedElseIfChainsGeneric",
        96,
        [&]() {
            bool local_change = false;
            for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
                std::string outer_condition;
                if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition)) {
                    continue;
                }

                const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
                std::size_t else_line = lines->size();
                int depth = 1;
                for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                    const std::string_view trimmed = trim_line((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }
                    if (depth == 1 && trimmed == "else" &&
                        GetRenderedLuaIndent((*lines)[probe]) == outer_indent) {
                        else_line = probe;
                        break;
                    }
                    depth += GetRenderedLuaBlockDelta(trimmed);
                    if (depth == 0) {
                        break;
                    }
                }
                if (else_line >= lines->size()) {
                    continue;
                }

                const std::size_t nested_if_line = next_nonempty_noncomment(else_line + 1);
                if (nested_if_line >= lines->size() ||
                    GetRenderedLuaIndent((*lines)[nested_if_line]) != outer_indent) {
                    continue;
                }

                bool has_intermediate_nonempty = false;
                for (std::size_t probe = else_line + 1; probe < nested_if_line; ++probe) {
                    if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                        has_intermediate_nonempty = true;
                        break;
                    }
                }
                if (has_intermediate_nonempty) {
                    continue;
                }

                std::string nested_condition;
                if (!TryExtractRenderedIfCondition((*lines)[nested_if_line], &nested_condition)) {
                    continue;
                }

                const std::size_t nested_end =
                    FindRenderedCleanupBlockEnd(*lines, nested_if_line);
                if (nested_end >= lines->size() || trim_line((*lines)[nested_end]) != "end") {
                    continue;
                }

                const std::size_t outer_end =
                    FindRenderedCleanupBlockEnd(*lines, line_index);
                if (outer_end >= lines->size() || trim_line((*lines)[outer_end]) != "end" ||
                    nested_end >= outer_end) {
                    continue;
                }

                bool tail_has_code = false;
                for (std::size_t probe = nested_end + 1; probe < outer_end; ++probe) {
                    const std::string_view trimmed = trim_line((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }
                    if (GetRenderedLuaIndent((*lines)[probe]) == outer_indent &&
                        (trimmed == "else" ||
                         (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")))) {
                        continue;
                    }
                    tail_has_code = true;
                    break;
                }
                if (tail_has_code) {
                    continue;
                }

                (*lines)[nested_if_line] = outer_indent + "elseif " + nested_condition + " then";
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(else_line));

                std::size_t adjusted_nested_end = nested_end;
                if (adjusted_nested_end > else_line) {
                    --adjusted_nested_end;
                }
                lines->erase(lines->begin() +
                             static_cast<std::ptrdiff_t>(adjusted_nested_end));

                changed = true;
                local_change = true;
                if (line_index != 0) {
                    --line_index;
                }
                break;
            }
            return local_change;
        });

    return changed;
}

bool SimplifyRenderedLuaTerminalElseIfNestedIfBranches(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_noncomment =
        [&](std::size_t start, std::size_t limit) -> std::size_t {
        for (std::size_t probe = start; probe < limit && probe < lines->size(); ++probe) {
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return limit;
    };

    struct BranchSpan {
        std::size_t header_line = 0;
        std::size_t body_start = 0;
        std::size_t body_end = 0;
    };

    bool changed = false;
    RunRenderedLuaCleanupPassLoop(
        "SimplifyRenderedLuaTerminalElseIfNestedIfBranches",
        96,
        [&]() {
            bool local_change = false;
            for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
                const std::string_view chain_head = trim_line((*lines)[line_index]);
                if (!chain_head.starts_with("if ") || !chain_head.ends_with(" then")) {
                    continue;
                }

                const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
                if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
                    continue;
                }

                std::vector<BranchSpan> branches;
                std::size_t current_header = line_index;
                std::size_t body_start = line_index + 1;
                int depth = 1;
                for (std::size_t probe = line_index + 1; probe < block_end; ++probe) {
                    const std::string_view trimmed = trim_line((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }

                    if (depth == 1 &&
                        (trimmed == "else" ||
                         (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")))) {
                        branches.push_back({current_header, body_start, probe});
                        current_header = probe;
                        body_start = probe + 1;
                        continue;
                    }

                    depth += GetRenderedLuaBlockDelta(trimmed);
                    if (depth == 0) {
                        break;
                    }
                }
                branches.push_back({current_header, body_start, block_end});

                bool branch_changed = false;
                for (const BranchSpan& branch : branches) {
                    const std::string_view branch_header = trim_line((*lines)[branch.header_line]);
                    if (!branch_header.starts_with("elseif ") ||
                        !branch_header.ends_with(" then")) {
                        continue;
                    }

                    const std::size_t terminal_line =
                        next_nonempty_noncomment(branch.body_start, branch.body_end);
                    if (terminal_line >= branch.body_end ||
                        !StartsWithLuaKeyword(trim_line((*lines)[terminal_line]), "return")) {
                        continue;
                    }

                    const std::size_t nested_if_line =
                        next_nonempty_noncomment(terminal_line + 1, branch.body_end);
                    if (nested_if_line >= branch.body_end) {
                        continue;
                    }

                    const std::string_view nested_if_trimmed = trim_line((*lines)[nested_if_line]);
                    if (!nested_if_trimmed.starts_with("if ") ||
                        !nested_if_trimmed.ends_with(" then")) {
                        continue;
                    }

                    std::string nested_condition;
                    if (!TryExtractRenderedIfCondition((*lines)[nested_if_line],
                                                       &nested_condition)) {
                        continue;
                    }

                    const std::size_t nested_end =
                        FindRenderedCleanupBlockEnd(*lines, nested_if_line);
                    if (nested_end >= branch.body_end ||
                        trim_line((*lines)[nested_end]) != "end") {
                        continue;
                    }

                    bool tail_has_code = false;
                    for (std::size_t probe = nested_end + 1; probe < branch.body_end; ++probe) {
                        if (!trim_line((*lines)[probe]).empty()) {
                            tail_has_code = true;
                            break;
                        }
                    }
                    if (tail_has_code) {
                        continue;
                    }

                    const std::string branch_indent(GetRenderedLuaIndent(
                        (*lines)[branch.header_line]));
                    (*lines)[nested_if_line] =
                        branch_indent + "elseif " + nested_condition + " then";
                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(nested_end));

                    changed = true;
                    local_change = true;
                    branch_changed = true;
                    if (line_index != 0) {
                        --line_index;
                    }
                    break;
                }

                if (branch_changed) {
                    break;
                }
            }
            return local_change;
        });

    return changed;
}
