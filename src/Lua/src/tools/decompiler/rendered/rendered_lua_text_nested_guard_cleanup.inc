bool SimplifyRenderedLuaNestedAssignedGuardChains(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_positive_if_condition = [](std::string_view line,
                                                std::string* condition) {
        if (condition == nullptr) {
            return false;
        }
        std::string extracted;
        if (!TryExtractRenderedIfCondition(line, &extracted)) {
            return false;
        }
        extracted = StripRenderedWholeParentheses(TrimLuaWhitespace(extracted), nullptr);
        if (extracted.empty() || StartsWithLuaKeyword(extracted, "not")) {
            return false;
        }
        *condition = std::move(extracted);
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !seed_is_local || !IsRenderTemporaryName(seed_lhs)) {
            continue;
        }

        std::string outer_condition;
        if (!parse_positive_if_condition((*lines)[line_index + 1], &outer_condition) ||
            outer_condition != seed_lhs) {
            continue;
        }

        bool inner_assign_is_local = false;
        std::string inner_assign_lhs;
        std::string inner_assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &inner_assign_is_local,
                                               &inner_assign_lhs,
                                               &inner_assign_rhs) ||
            inner_assign_is_local || inner_assign_lhs != seed_lhs) {
            continue;
        }

        std::string inner_condition;
        if (!parse_positive_if_condition((*lines)[line_index + 3], &inner_condition)) {
            continue;
        }

        const std::string normalized_inner_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(inner_assign_rhs), nullptr);
        if (normalized_inner_rhs.empty() || inner_condition != normalized_inner_rhs) {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index + 1);
        const std::size_t inner_end = FindRenderedCleanupBlockEnd(*lines, line_index + 3);
        if (outer_end >= lines->size() || inner_end >= lines->size() ||
            inner_end + 1 != outer_end ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index + 1, outer_end) ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index + 3, inner_end)) {
            continue;
        }

        bool temp_used_inside = false;
        for (std::size_t probe = line_index + 4; probe < outer_end; ++probe) {
            if (CountIdentifierUses((*lines)[probe], seed_lhs) != 0) {
                temp_used_inside = true;
                break;
            }
        }
        if (temp_used_inside) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        const std::string combined_condition =
            RenderLogicalExpression(
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr),
                "and",
                normalized_inner_rhs);
        if (combined_condition.empty()) {
            continue;
        }

        (*lines)[line_index + 1] = outer_indent + "if " + combined_condition + " then";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(outer_end));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedTrueFlagSearchElse(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 9 < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition)) {
            continue;
        }

        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            seed_is_local ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr) != "true" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end" ||
            !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }

        const std::string_view loop_header =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
        if (!StartsWithLuaKeyword(loop_header, "for") || !loop_header.ends_with(" do")) {
            continue;
        }

        const std::size_t loop_end = FindRenderedCleanupBlockEnd(*lines, line_index + 3);
        if (loop_end >= lines->size()) {
            continue;
        }

        std::size_t inner_if_line = lines->size();
        for (std::size_t probe = line_index + 4; probe < loop_end; ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (!probe_trimmed.empty()) {
                inner_if_line = probe;
                break;
            }
        }
        if (inner_if_line >= loop_end) {
            continue;
        }

        std::string inner_condition;
        if (!TryExtractRenderedIfCondition((*lines)[inner_if_line], &inner_condition)) {
            continue;
        }

        const std::size_t inner_end = FindRenderedCleanupBlockEnd(*lines, inner_if_line);
        if (inner_end >= lines->size() || inner_end + 1 != loop_end ||
            !RenderedLuaBlockHasTopLevelElse(*lines, inner_if_line, inner_end)) {
            continue;
        }

        std::size_t else_line = lines->size();
        int depth = 1;
        for (std::size_t probe = inner_if_line + 1; probe < inner_end; ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (depth == 1 && probe_trimmed == "else") {
                else_line = probe;
                break;
            }
            depth += GetRenderedLuaBlockDelta(probe_trimmed);
        }
        if (else_line >= inner_end || else_line != inner_end - 1) {
            continue;
        }

        bool loop_assign_is_local = false;
        std::string loop_assign_lhs;
        std::string loop_assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[inner_if_line + 1],
                                               &loop_assign_is_local,
                                               &loop_assign_lhs,
                                               &loop_assign_rhs) ||
            loop_assign_is_local || loop_assign_lhs != seed_lhs ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(loop_assign_rhs), nullptr) != "true" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_if_line + 2])) != "break") {
            continue;
        }

        if (FindRenderedLastNonEmptyLine(*lines, inner_if_line + 1, else_line) !=
            inner_if_line + 2) {
            continue;
        }

        std::size_t next_nonempty = lines->size();
        for (std::size_t probe = loop_end + 1; probe < lines->size(); ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (!probe_trimmed.empty()) {
                next_nonempty = probe;
                break;
            }
        }
        if (next_nonempty >= lines->size()) {
            continue;
        }

        std::string next_condition;
        if (!TryExtractRenderedIfCondition((*lines)[next_nonempty], &next_condition) ||
            CountIdentifierUses(next_condition, seed_lhs) == 0) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::vector<std::string> replacement = {
            (*lines)[line_index],
            (*lines)[line_index + 1],
            outer_indent + "else",
            (*lines)[line_index + 3],
            (*lines)[inner_if_line],
            (*lines)[inner_if_line + 1],
            (*lines)[inner_if_line + 2],
            (*lines)[inner_end],
            (*lines)[loop_end],
            outer_indent + "end",
        };

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(loop_end + 1));
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

bool SimplifyRenderedLuaNestedGuardedConditionChains(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition)) {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (outer_end >= lines->size() || outer_end <= line_index + 2 ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, outer_end)) {
            continue;
        }

        std::size_t nested_start = lines->size();
        for (std::size_t probe = line_index + 1; probe < outer_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            nested_start = probe;
            break;
        }
        if (nested_start >= outer_end) {
            continue;
        }
        for (std::size_t probe = line_index + 1; probe < nested_start; ++probe) {
            if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                nested_start = lines->size();
                break;
            }
        }
        if (nested_start >= outer_end) {
            continue;
        }

        std::string nested_condition;
        if (!TryExtractRenderedIfCondition((*lines)[nested_start], &nested_condition)) {
            continue;
        }

        const std::size_t nested_end = FindRenderedCleanupBlockEnd(*lines, nested_start);
        if (nested_end >= lines->size() || nested_end >= outer_end ||
            RenderedLuaBlockHasTopLevelElse(*lines, nested_start, nested_end)) {
            continue;
        }

        bool has_trailing_code = false;
        for (std::size_t probe = nested_end + 1; probe < outer_end; ++probe) {
            if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                has_trailing_code = true;
                break;
            }
        }
        if (has_trailing_code) {
            continue;
        }

        const std::string combined_condition =
            RenderLogicalExpression(outer_condition, "and", nested_condition);
        if (combined_condition.empty()) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string outer_body_indent = outer_indent + "  ";
        if (nested_end <= nested_start + 1) {
            continue;
        }

        const std::string nested_body_indent =
            std::string(GetRenderedLuaIndent((*lines)[nested_start + 1]));
        (*lines)[line_index] = outer_indent + "if " + combined_condition + " then";
        for (std::size_t probe = nested_start + 1; probe < nested_end; ++probe) {
            if ((*lines)[probe].starts_with(nested_body_indent)) {
                (*lines)[probe].replace(0, nested_body_indent.size(), outer_body_indent);
            }
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(nested_end));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(nested_start));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}
