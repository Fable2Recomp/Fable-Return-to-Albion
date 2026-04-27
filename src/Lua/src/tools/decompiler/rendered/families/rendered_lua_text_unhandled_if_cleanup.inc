bool SimplifyRenderedLuaUnhandledStateIfComments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto parse_elseif_condition = [](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("elseif ") || !trimmed.ends_with(" then")) {
            return false;
        }

        const std::string_view extracted =
            TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        std::size_t branch_line = line_index + 1;
        while (branch_line < lines->size() && trim_line((*lines)[branch_line]).empty()) {
            ++branch_line;
        }
        if (branch_line >= lines->size()) {
            continue;
        }

        std::string branch_condition;
        if (!parse_elseif_condition((*lines)[branch_line], &branch_condition)) {
            continue;
        }

        const std::size_t and_pos = branch_condition.find(" and ");
        if (and_pos == std::string::npos) {
            continue;
        }

        const std::string state_condition =
            std::string(TrimLuaWhitespace(branch_condition.substr(0, and_pos)));
        const std::string nested_condition =
            std::string(TrimLuaWhitespace(branch_condition.substr(and_pos + 5)));
        if (state_condition.empty() || nested_condition.empty()) {
            continue;
        }
        if (state_condition.find("State") == std::string::npos &&
            state_condition.find("ThreadVar") == std::string::npos) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[branch_line]));
        if (indent.empty()) {
            continue;
        }

        std::size_t block_end = lines->size();
        for (std::size_t probe = branch_line + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (GetRenderedLuaIndent((*lines)[probe]) == indent &&
                (trimmed.starts_with("elseif ") || trimmed == "end")) {
                block_end = probe;
                break;
            }
        }
        if (block_end >= lines->size() || block_end <= branch_line + 1) {
            continue;
        }

        std::size_t body_first = branch_line + 1;
        while (body_first < block_end && trim_line((*lines)[body_first]).empty()) {
            ++body_first;
        }
        if (body_first >= block_end) {
            continue;
        }

        const std::string body_indent(GetRenderedLuaIndent((*lines)[body_first]));
        if (body_indent.size() <= indent.size() || !body_indent.starts_with(indent)) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.reserve(block_end - line_index + 2);
        replacement.push_back(indent + "elseif " + state_condition + " then");
        replacement.push_back(indent + "  if " + nested_condition + " then");
        for (std::size_t probe = branch_line + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                replacement.push_back((*lines)[probe]);
                continue;
            }

            const std::string_view current_indent = GetRenderedLuaIndent((*lines)[probe]);
            if (current_indent.starts_with(body_indent)) {
                replacement.push_back(
                    indent + "  " +
                    std::string(current_indent.substr(body_indent.size())) +
                    std::string((*lines)[probe].substr(current_indent.size())));
            } else {
                replacement.push_back(indent + "  " + std::string(trimmed));
            }
        }
        replacement.push_back(indent + "  end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(block_end));
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

bool SimplifyRenderedLuaUnhandledIfCommentBranchChains(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct BranchSpan {
        std::string condition;
        std::size_t body_start = 0;
        std::size_t body_end = 0;
    };

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto join_boolean_parts = [](const std::vector<std::string>& parts,
                                       std::size_t begin,
                                       std::size_t end,
                                       const char* op) -> std::string {
        if (begin >= end || end > parts.size()) {
            return {};
        }

        std::string expression = parts[begin];
        for (std::size_t index = begin + 1; index < end; ++index) {
            expression = RenderLogicalExpression(expression, op, parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };

    const auto collect_condition_anchors = [&](const auto& self,
                                               std::string_view expression,
                                               std::unordered_set<std::string>* anchors) -> void {
        if (anchors == nullptr) {
            return;
        }

        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        if (normalized.empty()) {
            return;
        }

        std::vector<std::string> parts;
        if (SplitRenderedTopLevelBooleanChain(normalized, "or", &parts) ||
            SplitRenderedTopLevelBooleanChain(normalized, "and", &parts)) {
            for (const std::string& part : parts) {
                self(self, part, anchors);
            }
            return;
        }

        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op)) {
            const std::string lhs = NormalizeRenderedConditionOperand(
                normalized.substr(0, compare_position), nullptr);
            if (!lhs.empty()) {
                anchors->insert(lhs);
            }
            return;
        }

        const std::string fallback = NormalizeRenderedConditionExpression(normalized, nullptr);
        if (!fallback.empty()) {
            anchors->insert(fallback);
        }
    };

    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start >= body_end || body_end > lines->size()) {
            return false;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return false;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }

            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }

            if (stripped_trimmed.empty()) {
                continue;
            }

            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
        return true;
    };

    const auto collect_branch_chain = [&](const auto& self,
                                          std::size_t if_line,
                                          std::vector<BranchSpan>* branches,
                                          std::size_t* chain_end) -> bool {
        if (branches == nullptr || chain_end == nullptr || if_line >= lines->size()) {
            return false;
        }

        std::string current_condition;
        if (!parse_if_like_condition((*lines)[if_line], &current_condition)) {
            return false;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
            return false;
        }

        std::size_t branch_body_start = if_line + 1;
        int depth = 1;
        for (std::size_t probe = if_line + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }

            if (depth == 1 && trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                if (probe <= branch_body_start) {
                    return false;
                }
                branches->push_back({current_condition, branch_body_start, probe});
                if (!parse_if_like_condition((*lines)[probe], &current_condition)) {
                    return false;
                }
                branch_body_start = probe + 1;
                continue;
            }

            if (depth == 1 && trimmed == "else") {
                if (probe <= branch_body_start) {
                    return false;
                }
                branches->push_back({current_condition, branch_body_start, probe});

                std::size_t nested_if = probe + 1;
                while (nested_if < block_end && trim_line((*lines)[nested_if]).empty()) {
                    ++nested_if;
                }
                if (nested_if >= block_end) {
                    return false;
                }

                std::vector<BranchSpan> nested_branches;
                std::size_t nested_end = nested_if;
                if (!self(self, nested_if, &nested_branches, &nested_end)) {
                    return false;
                }
                if (nested_branches.empty()) {
                    return false;
                }

                for (std::size_t tail = nested_end + 1; tail < block_end; ++tail) {
                    if (!trim_line((*lines)[tail]).empty()) {
                        return false;
                    }
                }

                branches->insert(branches->end(), nested_branches.begin(), nested_branches.end());
                *chain_end = block_end;
                return true;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
        }

        if (block_end <= branch_body_start) {
            return false;
        }
        branches->push_back({current_condition, branch_body_start, block_end});
        *chain_end = block_end;
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        std::size_t if_line = line_index + 1;
        while (if_line < lines->size() && trim_line((*lines)[if_line]).empty()) {
            ++if_line;
        }
        if (if_line >= lines->size()) {
            continue;
        }

        std::vector<BranchSpan> branches;
        std::size_t chain_end = if_line;
        if (!collect_branch_chain(collect_branch_chain, if_line, &branches, &chain_end) ||
            branches.size() < 2) {
            continue;
        }

        std::vector<std::string> first_parts;
        if (!SplitRenderedTopLevelBooleanChain(branches.front().condition, "and", &first_parts) ||
            first_parts.size() < 2) {
            continue;
        }

        std::unordered_set<std::string> later_anchors;
        for (std::size_t branch_index = 1; branch_index < branches.size(); ++branch_index) {
            collect_condition_anchors(collect_condition_anchors,
                                      branches[branch_index].condition,
                                      &later_anchors);
        }
        if (later_anchors.empty()) {
            continue;
        }

        std::size_t branch_suffix_start = first_parts.size();
        for (std::size_t part_index = 0; part_index < first_parts.size(); ++part_index) {
            std::unordered_set<std::string> part_anchors;
            collect_condition_anchors(collect_condition_anchors,
                                      first_parts[part_index],
                                      &part_anchors);
            const bool matches_later = std::any_of(
                part_anchors.begin(), part_anchors.end(), [&](const std::string& anchor) {
                    return later_anchors.find(anchor) != later_anchors.end();
                });
            if (matches_later) {
                branch_suffix_start = part_index;
                break;
            }
        }
        if (branch_suffix_start == 0 || branch_suffix_start >= first_parts.size()) {
            continue;
        }

        bool suffix_is_branch_specific = true;
        for (std::size_t part_index = branch_suffix_start; part_index < first_parts.size();
             ++part_index) {
            std::unordered_set<std::string> part_anchors;
            collect_condition_anchors(collect_condition_anchors,
                                      first_parts[part_index],
                                      &part_anchors);
            const bool matches_later = std::any_of(
                part_anchors.begin(), part_anchors.end(), [&](const std::string& anchor) {
                    return later_anchors.find(anchor) != later_anchors.end();
                });
            if (!matches_later) {
                suffix_is_branch_specific = false;
                break;
            }
        }
        if (!suffix_is_branch_specific) {
            continue;
        }

        const std::string outer_condition =
            join_boolean_parts(first_parts, 0, branch_suffix_start, "and");
        const std::string first_branch_condition =
            join_boolean_parts(first_parts, branch_suffix_start, first_parts.size(), "and");
        if (outer_condition.empty() || first_branch_condition.empty()) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[if_line]));
        const std::string branch_indent = outer_indent + "  ";
        const std::string body_indent = branch_indent + "  ";
        std::vector<std::string> replacement;
        replacement.push_back(outer_indent + "if " + outer_condition + " then");
        replacement.push_back(branch_indent + "if " + first_branch_condition + " then");
        if (!append_reindented_body(&replacement,
                                    branches.front().body_start,
                                    branches.front().body_end,
                                    body_indent)) {
            continue;
        }

        bool valid = true;
        for (std::size_t branch_index = 1; branch_index < branches.size(); ++branch_index) {
            replacement.push_back(branch_indent + "elseif " + branches[branch_index].condition +
                                  " then");
            if (!append_reindented_body(&replacement,
                                        branches[branch_index].body_start,
                                        branches[branch_index].body_end,
                                        body_indent)) {
                valid = false;
                break;
            }
        }
        if (!valid) {
            continue;
        }

        replacement.push_back(branch_indent + "end");
        replacement.push_back(outer_indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(chain_end + 1));
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

bool SimplifyRenderedLuaUnhandledIfCommentLeadingSuffixes(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct BranchSpan {
        std::string condition;
        std::size_t body_start = 0;
        std::size_t body_end = 0;
    };

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto join_boolean_parts = [](const std::vector<std::string>& parts,
                                       std::size_t begin,
                                       std::size_t end) -> std::string {
        if (begin >= end || end > parts.size()) {
            return {};
        }

        std::string expression = parts[begin];
        for (std::size_t index = begin + 1; index < end; ++index) {
            expression = RenderLogicalExpression(expression, "and", parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start >= body_end || body_end > lines->size()) {
            return false;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return false;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }

            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }

            if (stripped_trimmed.empty()) {
                continue;
            }

            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        std::size_t if_line = line_index + 1;
        while (if_line < lines->size() && trim_line((*lines)[if_line]).empty()) {
            ++if_line;
        }
        if (if_line >= lines->size()) {
            continue;
        }

        std::string first_condition;
        if (!parse_if_like_condition((*lines)[if_line], &first_condition)) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
            continue;
        }

        std::vector<BranchSpan> branches;
        std::size_t branch_body_start = if_line + 1;
        std::string current_condition = first_condition;
        int depth = 1;
        bool saw_else = false;
        for (std::size_t probe = if_line + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                saw_else = true;
                break;
            }
            if (depth == 1 && trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                if (probe <= branch_body_start) {
                    saw_else = true;
                    break;
                }
                branches.push_back({current_condition, branch_body_start, probe});
                if (!parse_if_like_condition((*lines)[probe], &current_condition)) {
                    saw_else = true;
                    break;
                }
                branch_body_start = probe + 1;
                continue;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }

        if (saw_else || branch_body_start >= block_end) {
            continue;
        }
        branches.push_back({current_condition, branch_body_start, block_end});
        if (branches.size() < 2) {
            continue;
        }

        std::vector<std::string> first_parts;
        if (!SplitRenderedTopLevelBooleanChain(first_condition, "and", &first_parts) ||
            first_parts.size() < 2) {
            continue;
        }

        std::vector<std::vector<std::string>> later_parts;
        later_parts.reserve(branches.size() - 1);
        for (std::size_t branch_index = 1; branch_index < branches.size(); ++branch_index) {
            std::vector<std::string> parts;
            if (!SplitRenderedTopLevelBooleanChain(branches[branch_index].condition,
                                                   "and",
                                                   &parts) ||
                parts.size() < 2) {
                parts.clear();
            }
            later_parts.push_back(std::move(parts));
        }

        std::size_t common_suffix = 0;
        for (const std::vector<std::string>& parts : later_parts) {
            const std::size_t limit = std::min(first_parts.size(), parts.size());
            for (std::size_t count = limit; count >= 1; --count) {
                bool matches = true;
                for (std::size_t offset = 0; offset < count; ++offset) {
                    const std::string left = NormalizeRenderedConditionExpression(
                        first_parts[first_parts.size() - count + offset], nullptr);
                    const std::string right = NormalizeRenderedConditionExpression(
                        parts[parts.size() - count + offset], nullptr);
                    if (left.empty() || right.empty() || left != right) {
                        matches = false;
                        break;
                    }
                }
                if (matches) {
                    common_suffix = std::max(common_suffix, count);
                    break;
                }
                if (count == 1) {
                    break;
                }
            }
        }
        if (common_suffix == 0 || common_suffix >= first_parts.size()) {
            continue;
        }

        const std::string outer_condition =
            join_boolean_parts(first_parts, 0, first_parts.size() - common_suffix);
        const std::string nested_condition =
            join_boolean_parts(first_parts, first_parts.size() - common_suffix, first_parts.size());
        if (outer_condition.empty() || nested_condition.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[if_line]));
        const std::string nested_indent = indent + "  ";
        const std::string nested_body_indent = nested_indent + "  ";
        std::vector<std::string> replacement;
        replacement.push_back(indent + "if " + outer_condition + " then");
        replacement.push_back(nested_indent + "if " + nested_condition + " then");
        if (!append_reindented_body(&replacement,
                                    branches.front().body_start,
                                    branches.front().body_end,
                                    nested_body_indent)) {
            continue;
        }
        replacement.push_back(nested_indent + "end");
        for (std::size_t branch_index = 1; branch_index < branches.size(); ++branch_index) {
            replacement.push_back(indent + "elseif " + branches[branch_index].condition + " then");
            if (!append_reindented_body(&replacement,
                                        branches[branch_index].body_start,
                                        branches[branch_index].body_end,
                                        nested_indent)) {
                replacement.clear();
                break;
            }
        }
        if (replacement.empty()) {
            continue;
        }
        replacement.push_back(indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(block_end + 1));
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

bool SimplifyRenderedLuaUnhandledIfCommentNestedTailChains(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct BranchSpan {
        std::string condition;
        std::size_t body_start = 0;
        std::size_t body_end = 0;
    };

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start >= body_end || body_end > lines->size()) {
            return false;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return false;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }

            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }

            if (stripped_trimmed.empty()) {
                continue;
            }

            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
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
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };
    const auto collect_simple_branch_chain = [&](const auto& self,
                                                 std::size_t if_line,
                                                 std::vector<BranchSpan>* branches,
                                                 std::size_t* chain_end) -> bool {
        if (branches == nullptr || chain_end == nullptr || if_line >= lines->size()) {
            return false;
        }

        std::string current_condition;
        if (!parse_if_like_condition((*lines)[if_line], &current_condition)) {
            return false;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
            return false;
        }

        std::size_t branch_body_start = if_line + 1;
        int depth = 1;
        for (std::size_t probe = if_line + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                if (probe <= branch_body_start) {
                    return false;
                }
                branches->push_back({current_condition, branch_body_start, probe});
                if (!parse_if_like_condition((*lines)[probe], &current_condition)) {
                    return false;
                }
                branch_body_start = probe + 1;
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                return false;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }

        if (branch_body_start >= block_end) {
            return false;
        }
        branches->push_back({current_condition, branch_body_start, block_end});
        *chain_end = block_end;
        return branches->size() >= 2;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        const std::size_t outer_if_line = next_nonempty_noncomment(line_index + 1);
        if (outer_if_line >= lines->size()) {
            continue;
        }

        std::string outer_condition;
        if (!parse_if_like_condition((*lines)[outer_if_line], &outer_condition)) {
            continue;
        }

        std::size_t outer_else_line = lines->size();
        int depth = 1;
        for (std::size_t probe = outer_if_line + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                outer_else_line = probe;
                break;
            }
            if (GetRenderedLuaIndent((*lines)[probe]).empty() &&
                !LooksLikeRenderedControlBlockHeader(trimmed) && probe > outer_if_line + 1) {
                break;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }
        if (outer_else_line >= lines->size() || outer_else_line <= outer_if_line + 1) {
            continue;
        }

        const std::size_t inner_if_line = next_nonempty_noncomment(outer_else_line + 1);
        std::string inner_condition;
        if (inner_if_line >= lines->size() ||
            !parse_if_like_condition((*lines)[inner_if_line], &inner_condition)) {
            continue;
        }

        std::size_t inner_else_line = lines->size();
        depth = 1;
        for (std::size_t probe = inner_if_line + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                inner_else_line = probe;
                break;
            }
            if (GetRenderedLuaIndent((*lines)[probe]).empty() &&
                !LooksLikeRenderedControlBlockHeader(trimmed) && probe > inner_if_line + 1) {
                break;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }
        if (inner_else_line >= lines->size() || inner_else_line <= inner_if_line + 1) {
            continue;
        }

        const std::size_t nested_if_line = next_nonempty_noncomment(inner_else_line + 1);
        std::vector<BranchSpan> nested_branches;
        std::size_t nested_chain_end = nested_if_line;
        if (nested_if_line >= lines->size() ||
            !collect_simple_branch_chain(collect_simple_branch_chain,
                                         nested_if_line,
                                         &nested_branches,
                                         &nested_chain_end)) {
            continue;
        }

        std::size_t top_level_boundary = lines->size();
        for (std::size_t probe = nested_chain_end + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (GetRenderedLuaIndent((*lines)[probe]).empty() &&
                !LooksLikeRenderedControlBlockHeader(trimmed)) {
                top_level_boundary = probe;
                break;
            }
        }
        if (top_level_boundary >= lines->size()) {
            continue;
        }

        std::size_t replacement_end = top_level_boundary;
        while (replacement_end > nested_chain_end + 1) {
            const std::size_t probe = replacement_end - 1;
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                --replacement_end;
                continue;
            }
            if (trimmed == "end") {
                --replacement_end;
                continue;
            }
            break;
        }
        if (replacement_end <= nested_chain_end + 1) {
            replacement_end = top_level_boundary;
        }

        bool only_end_tail = true;
        for (std::size_t probe = nested_chain_end + 1; probe < top_level_boundary; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty() || trimmed == "end") {
                continue;
            }
            only_end_tail = false;
            break;
        }
        if (!only_end_tail) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[outer_if_line]));
        std::vector<std::string> replacement;
        replacement.push_back(indent + "if " + outer_condition + " then");
        if (!append_reindented_body(&replacement,
                                    outer_if_line + 1,
                                    outer_else_line,
                                    indent + "  ")) {
            continue;
        }
        replacement.push_back(indent + "elseif " + inner_condition + " then");
        if (!append_reindented_body(&replacement,
                                    inner_if_line + 1,
                                    inner_else_line,
                                    indent + "  ")) {
            continue;
        }
        for (const BranchSpan& branch : nested_branches) {
            replacement.push_back(indent + "elseif " + branch.condition + " then");
            if (!append_reindented_body(&replacement,
                                        branch.body_start,
                                        branch.body_end,
                                        indent + "  ")) {
                replacement.clear();
                break;
            }
        }
        if (replacement.empty()) {
            continue;
        }
        replacement.push_back(indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(top_level_boundary));
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

bool SimplifyRenderedLuaUnhandledIfCommentNilGuardSplits(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct BranchSpan {
        std::string condition;
        std::size_t body_start = 0;
        std::size_t body_end = 0;
    };

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
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start > body_end || body_end > lines->size()) {
            return false;
        }
        if (body_start == body_end) {
            return true;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return true;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }
            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }
            if (stripped_trimmed.empty()) {
                continue;
            }

            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
        return true;
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto join_boolean_parts = [](const std::vector<std::string>& parts,
                                       std::size_t begin,
                                       std::size_t end) -> std::string {
        if (begin >= end || end > parts.size()) {
            return {};
        }

        std::string expression = parts[begin];
        for (std::size_t index = begin + 1; index < end; ++index) {
            expression = RenderLogicalExpression(expression, "and", parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };
    const auto collect_simple_branch_chain = [&](const auto& self,
                                                 std::size_t if_line,
                                                 std::vector<BranchSpan>* branches,
                                                 std::size_t* chain_end) -> bool {
        if (branches == nullptr || chain_end == nullptr || if_line >= lines->size()) {
            return false;
        }

        std::string current_condition;
        if (!parse_if_like_condition((*lines)[if_line], &current_condition)) {
            return false;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
            return false;
        }

        std::size_t branch_body_start = if_line + 1;
        int depth = 1;
        for (std::size_t probe = if_line + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                if (probe <= branch_body_start) {
                    return false;
                }
                branches->push_back({current_condition, branch_body_start, probe});
                if (!parse_if_like_condition((*lines)[probe], &current_condition)) {
                    return false;
                }
                branch_body_start = probe + 1;
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                return false;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }

        if (branch_body_start >= block_end) {
            return false;
        }
        branches->push_back({current_condition, branch_body_start, block_end});
        *chain_end = block_end;
        return branches->size() >= 2;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        const std::size_t if_line = next_nonempty_noncomment(line_index + 1);
        if (if_line >= lines->size()) {
            continue;
        }

        std::string outer_condition;
        if (!TryExtractRenderedIfCondition((*lines)[if_line], &outer_condition)) {
            continue;
        }

        std::vector<std::string> outer_parts;
        if (!SplitRenderedTopLevelBooleanChain(outer_condition, "and", &outer_parts) ||
            outer_parts.size() < 2) {
            continue;
        }

        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(outer_parts.front(), &compare_position, &compare_op) ||
            compare_op != "~=") {
            continue;
        }

        const std::string nil_guard = std::string(TrimLuaWhitespace(outer_parts.front()));
        const std::string nil_guard_rhs = NormalizeRenderedConditionExpression(
            outer_parts.front().substr(compare_position + compare_op.size()), nullptr);
        const std::string guarded_condition =
            join_boolean_parts(outer_parts, 1, outer_parts.size());
        if (nil_guard_rhs != "nil" || guarded_condition.empty()) {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (outer_end >= lines->size() || trim_line((*lines)[outer_end]) != "end") {
            continue;
        }

        std::size_t nested_if_line = lines->size();
        for (std::size_t probe = if_line + 1; probe < outer_end; ++probe) {
            std::string nested_condition;
            if (TryExtractRenderedIfCondition((*lines)[probe], &nested_condition)) {
                nested_if_line = probe;
                break;
            }
        }
        if (nested_if_line >= outer_end) {
            continue;
        }

        std::vector<BranchSpan> nested_branches;
        std::size_t nested_end = nested_if_line;
        if (!collect_simple_branch_chain(collect_simple_branch_chain,
                                         nested_if_line,
                                         &nested_branches,
                                         &nested_end) ||
            nested_branches.size() != 2 || nested_end + 1 != outer_end) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[if_line]));
        std::vector<std::string> replacement;
        replacement.push_back(outer_indent + "if " + nil_guard + " then");
        replacement.push_back(outer_indent + "  if " + guarded_condition + " then");
        if (!append_reindented_body(
                &replacement, if_line + 1, nested_if_line, outer_indent + "    ")) {
            continue;
        }
        replacement.push_back(outer_indent + "    if " + nested_branches[0].condition + " then");
        if (!append_reindented_body(&replacement,
                                    nested_branches[0].body_start,
                                    nested_branches[0].body_end,
                                    outer_indent + "      ")) {
            continue;
        }
        replacement.push_back(outer_indent + "    end");
        replacement.push_back(outer_indent + "  end");
        replacement.push_back(outer_indent + "elseif " + nested_branches[1].condition + " then");
        if (!append_reindented_body(&replacement,
                                    nested_branches[1].body_start,
                                    nested_branches[1].body_end,
                                    outer_indent + "  ")) {
            continue;
        }
        replacement.push_back(outer_indent + "end");

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

bool SimplifyRenderedLuaUnhandledSparseNilGuardBranches(std::vector<std::string>* lines) {
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
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start >= body_end || body_end > lines->size()) {
            return false;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return false;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }
            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }
            if (stripped_trimmed.empty()) {
                continue;
            }
            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
        return true;
    };
    const auto collect_nil_guard_identifiers = [&](const auto& self,
                                                   std::string_view expression,
                                                   std::vector<std::string>* identifiers,
                                                   std::unordered_set<std::string>* seen) -> bool {
        if (identifiers == nullptr || seen == nullptr) {
            return false;
        }

        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        if (normalized.empty()) {
            return false;
        }

        std::vector<std::string> parts;
        if (SplitRenderedTopLevelBooleanChain(normalized, "or", &parts) ||
            SplitRenderedTopLevelBooleanChain(normalized, "and", &parts)) {
            for (const std::string& part : parts) {
                if (!self(self, part, identifiers, seen)) {
                    return false;
                }
            }
            return !identifiers->empty();
        }

        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op) ||
            (compare_op != "==" && compare_op != "~=")) {
            return false;
        }

        const std::string rhs = NormalizeRenderedConditionExpression(
            normalized.substr(compare_position + compare_op.size()), nullptr);
        if (rhs != "nil") {
            return false;
        }

        const std::string lhs = NormalizeRenderedConditionOperand(
            normalized.substr(0, compare_position), nullptr);
        if (lhs.empty()) {
            return false;
        }
        if (seen->insert(lhs).second) {
            identifiers->push_back(lhs);
        }
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        const std::size_t if_line = next_nonempty_noncomment(line_index + 1);
        std::string merged_condition;
        if (if_line >= lines->size() ||
            !TryExtractRenderedIfCondition((*lines)[if_line], &merged_condition)) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
            continue;
        }

        std::vector<std::string> guard_names;
        std::unordered_set<std::string> seen_guards;
        if (!collect_nil_guard_identifiers(collect_nil_guard_identifiers,
                                           merged_condition,
                                           &guard_names,
                                           &seen_guards) ||
            guard_names.size() < 2) {
            continue;
        }

        const std::string guarded_identifier = guard_names.back();
        bool body_mentions_guarded = false;
        bool body_mentions_other = false;
        for (std::size_t probe = if_line + 1; probe < block_end; ++probe) {
            const std::string_view text = (*lines)[probe];
            body_mentions_guarded =
                body_mentions_guarded || CountIdentifierUses(text, guarded_identifier) > 0;
            for (std::size_t index = 0; index + 1 < guard_names.size(); ++index) {
                if (CountIdentifierUses(text, guard_names[index]) > 0) {
                    body_mentions_other = true;
                    break;
                }
            }
            if (body_mentions_other) {
                break;
            }
        }
        if (!body_mentions_guarded || body_mentions_other) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[if_line]));
        const std::string body_indent = indent + "  ";
        std::vector<std::string> replacement;
        for (std::size_t index = 0; index + 1 < guard_names.size(); ++index) {
            replacement.push_back(indent + "if " + guard_names[index] + " ~= nil then");
            replacement.push_back(indent + "end");
        }
        replacement.push_back(indent + "if " + guarded_identifier + " ~= nil then");
        if (!append_reindented_body(&replacement, if_line + 1, block_end, body_indent)) {
            continue;
        }
        replacement.push_back(indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(block_end + 1));
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

bool SimplifyRenderedLuaUnhandledOuterOrNestedElseIfChains(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct BranchSpan {
        std::string condition;
        std::size_t body_start = 0;
        std::size_t body_end = 0;
    };

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
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start > body_end || body_end > lines->size()) {
            return false;
        }
        if (body_start == body_end) {
            return true;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return true;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }
            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }
            if (stripped_trimmed.empty()) {
                continue;
            }
            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
        return true;
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto parse_bool_equality = [&](std::string_view condition,
                                         std::string* lhs_out,
                                         bool* value_out) -> bool {
        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(condition, &compare_position, &compare_op) ||
            compare_op != "==") {
            return false;
        }
        const std::string lhs = NormalizeRenderedConditionOperand(
            condition.substr(0, compare_position), nullptr);
        const std::string rhs = NormalizeRenderedConditionExpression(
            condition.substr(compare_position + compare_op.size()), nullptr);
        if (lhs.empty()) {
            return false;
        }
        if (rhs == "true") {
            if (lhs_out != nullptr) {
                *lhs_out = lhs;
            }
            if (value_out != nullptr) {
                *value_out = true;
            }
            return true;
        }
        if (rhs == "false") {
            if (lhs_out != nullptr) {
                *lhs_out = lhs;
            }
            if (value_out != nullptr) {
                *value_out = false;
            }
            return true;
        }
        return false;
    };
    const auto join_boolean_parts = [](const std::vector<std::string>& parts,
                                       std::size_t begin,
                                       std::size_t end) -> std::string {
        if (begin >= end || end > parts.size()) {
            return {};
        }

        std::string expression = parts[begin];
        for (std::size_t index = begin + 1; index < end; ++index) {
            expression = RenderLogicalExpression(expression, "and", parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };
    const auto collect_simple_branch_chain = [&](const auto& self,
                                                 std::size_t if_line,
                                                 std::vector<BranchSpan>* branches,
                                                 std::size_t* chain_end) -> bool {
        if (branches == nullptr || chain_end == nullptr || if_line >= lines->size()) {
            return false;
        }

        std::string current_condition;
        if (!parse_if_like_condition((*lines)[if_line], &current_condition)) {
            return false;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
            return false;
        }

        std::size_t branch_body_start = if_line + 1;
        int depth = 1;
        for (std::size_t probe = if_line + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                branches->push_back({current_condition, branch_body_start, probe});
                if (!parse_if_like_condition((*lines)[probe], &current_condition)) {
                    return false;
                }
                branch_body_start = probe + 1;
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                return false;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }

        branches->push_back({current_condition, branch_body_start, block_end});
        *chain_end = block_end;
        return branches->size() >= 2;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        const std::size_t outer_if_line = next_nonempty_noncomment(line_index + 1);
        std::string outer_condition;
        if (outer_if_line >= lines->size() ||
            !TryExtractRenderedIfCondition((*lines)[outer_if_line], &outer_condition)) {
            continue;
        }

        std::vector<std::string> outer_parts;
        if (!SplitRenderedTopLevelBooleanChain(outer_condition, "or", &outer_parts) ||
            outer_parts.size() != 2) {
            continue;
        }

        std::string positive_lhs;
        bool positive_value = false;
        if (!parse_bool_equality(outer_parts[1], &positive_lhs, &positive_value) ||
            !positive_value) {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, outer_if_line);
        if (outer_end >= lines->size() || trim_line((*lines)[outer_end]) != "end") {
            continue;
        }

        const std::size_t prefix_start = next_nonempty_noncomment(outer_if_line + 1);
        if (prefix_start >= outer_end) {
            continue;
        }
        const std::size_t nested_if_line = next_nonempty_noncomment(prefix_start + 1);
        std::string nested_condition;
        if (nested_if_line >= outer_end ||
            !TryExtractRenderedIfCondition((*lines)[nested_if_line], &nested_condition)) {
            continue;
        }

        std::vector<BranchSpan> nested_branches;
        std::size_t nested_chain_end = nested_if_line;
        if (!collect_simple_branch_chain(collect_simple_branch_chain,
                                         next_nonempty_noncomment(nested_if_line + 1),
                                         &nested_branches,
                                         &nested_chain_end) ||
            nested_branches.size() != 3 ||
            nested_chain_end + 2 != outer_end) {
            continue;
        }

        std::vector<std::string> fallback_parts;
        if (!SplitRenderedTopLevelBooleanChain(nested_branches[2].condition, "and", &fallback_parts) ||
            fallback_parts.size() < 2) {
            continue;
        }

        std::string fallback_lhs;
        bool fallback_value = true;
        if (!parse_bool_equality(fallback_parts.front(), &fallback_lhs, &fallback_value) ||
            fallback_lhs != positive_lhs || fallback_value) {
            continue;
        }

        const std::string fallback_condition =
            join_boolean_parts(fallback_parts, 1, fallback_parts.size());
        if (fallback_condition.empty()) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[outer_if_line]));
        std::vector<std::string> replacement;
        replacement.push_back(outer_indent + "if " + std::string(TrimLuaWhitespace(outer_parts[1])) +
                              " then");
        if (!append_reindented_body(&replacement, prefix_start, nested_if_line, outer_indent + "  ")) {
            continue;
        }
        replacement.push_back(outer_indent + "  if " + nested_condition + " then");
        if (!append_reindented_body(&replacement,
                                    nested_branches[0].body_start,
                                    nested_branches[0].body_end,
                                    outer_indent + "    ")) {
            continue;
        }
        replacement.push_back(outer_indent + "  end");
        replacement.push_back(outer_indent + "elseif " + nested_branches[1].condition + " then");
        if (!append_reindented_body(&replacement,
                                    nested_branches[1].body_start,
                                    nested_branches[1].body_end,
                                    outer_indent + "  ")) {
            continue;
        }
        replacement.push_back(outer_indent + "elseif " + fallback_condition + " then");
        if (!append_reindented_body(&replacement,
                                    nested_branches[2].body_start,
                                    nested_branches[2].body_end,
                                    outer_indent + "  ")) {
            continue;
        }
        replacement.push_back(outer_indent + "end");

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

bool SimplifyRenderedLuaUnhandledFlaggedElseIfChains(std::vector<std::string>* lines) {
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
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start > body_end || body_end > lines->size()) {
            return false;
        }
        if (body_start == body_end) {
            return true;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return true;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }
            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }
            if (stripped_trimmed.empty()) {
                continue;
            }
            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
        return true;
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto parse_bool_equality = [&](std::string_view condition,
                                         std::string* lhs_out,
                                         bool* value_out) -> bool {
        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(condition, &compare_position, &compare_op) ||
            compare_op != "==") {
            return false;
        }
        const std::string lhs = NormalizeRenderedConditionOperand(
            condition.substr(0, compare_position), nullptr);
        const std::string rhs = NormalizeRenderedConditionExpression(
            condition.substr(compare_position + compare_op.size()), nullptr);
        if (lhs.empty()) {
            return false;
        }
        if (rhs == "true") {
            if (lhs_out != nullptr) {
                *lhs_out = lhs;
            }
            if (value_out != nullptr) {
                *value_out = true;
            }
            return true;
        }
        if (rhs == "false") {
            if (lhs_out != nullptr) {
                *lhs_out = lhs;
            }
            if (value_out != nullptr) {
                *value_out = false;
            }
            return true;
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        const std::size_t combined_if_line = next_nonempty_noncomment(line_index + 1);
        std::string combined_condition;
        if (combined_if_line >= lines->size() ||
            !parse_if_like_condition((*lines)[combined_if_line], &combined_condition)) {
            continue;
        }

        const std::size_t combined_end = FindRenderedCleanupBlockEnd(*lines, combined_if_line);
        if (combined_end >= lines->size() || trim_line((*lines)[combined_end]) != "end") {
            continue;
        }

        const std::size_t nested_flag_if_line = next_nonempty_noncomment(combined_if_line + 1);
        std::string nested_flag_condition;
        if (nested_flag_if_line >= combined_end ||
            !parse_if_like_condition((*lines)[nested_flag_if_line], &nested_flag_condition)) {
            continue;
        }

        std::string flag_lhs;
        bool flag_value = true;
        if (!parse_bool_equality(nested_flag_condition, &flag_lhs, &flag_value) || flag_value) {
            continue;
        }

        std::size_t sibling_if_line = lines->size();
        std::size_t cursor = combined_end + 1;
        bool saw_leaked_end = false;
        while (cursor < lines->size()) {
            const std::size_t probe = next_nonempty_noncomment(cursor);
            if (probe >= lines->size()) {
                break;
            }
            if (trim_line((*lines)[probe]) == "end") {
                saw_leaked_end = true;
                cursor = probe + 1;
                continue;
            }
            sibling_if_line = probe;
            break;
        }
        if (!saw_leaked_end || sibling_if_line >= lines->size()) {
            continue;
        }

        std::string sibling_condition;
        if (!parse_if_like_condition((*lines)[sibling_if_line], &sibling_condition)) {
            continue;
        }

        std::vector<std::string> sibling_parts;
        if (!SplitRenderedTopLevelBooleanChain(sibling_condition, "and", &sibling_parts) ||
            sibling_parts.size() < 2) {
            continue;
        }

        std::string sibling_lhs;
        bool sibling_value = false;
        if (!parse_bool_equality(sibling_parts.front(), &sibling_lhs, &sibling_value) ||
            sibling_lhs != flag_lhs || !sibling_value) {
            continue;
        }

        const std::size_t sibling_end = FindRenderedCleanupBlockEnd(*lines, sibling_if_line);
        if (sibling_end >= lines->size() || trim_line((*lines)[sibling_end]) != "end") {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[combined_if_line]));
        const std::string nested_indent = indent + "  ";
        std::vector<std::string> replacement;
        replacement.push_back(indent + "if " + flag_lhs + " == false then");
        if (!append_reindented_body(
                &replacement, combined_if_line, combined_end + 1, nested_indent)) {
            continue;
        }
        replacement.push_back(indent + "elseif " + sibling_condition + " then");
        if (!append_reindented_body(
                &replacement, sibling_if_line + 1, sibling_end, nested_indent)) {
            continue;
        }
        replacement.push_back(indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(sibling_end + 1));
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

bool SimplifyRenderedLuaUnhandledStateYieldCommentLoops(std::vector<std::string>* lines) {
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
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };
    const auto previous_nonempty_noncomment = [&](std::size_t start_exclusive) -> std::size_t {
        std::size_t probe = start_exclusive;
        while (probe != 0) {
            --probe;
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return lines->size();
    };
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start >= body_end || body_end > lines->size()) {
            return false;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return false;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }

            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }

            if (stripped_trimmed.empty()) {
                continue;
            }

            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
        return true;
    };
    const auto join_boolean_parts = [](const std::vector<std::string>& parts,
                                       std::size_t begin,
                                       std::size_t end) -> std::string {
        if (begin >= end || end > parts.size()) {
            return {};
        }

        std::string expression = parts[begin];
        for (std::size_t index = begin + 1; index < end; ++index) {
            expression = RenderLogicalExpression(expression, "and", parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto condition_matches_state = [&](std::string_view condition,
                                             std::string_view expected_lhs,
                                             std::string_view expected_rhs) {
        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(condition, &compare_position, &compare_op) ||
            compare_op != "==") {
            return false;
        }

        const std::string lhs = NormalizeRenderedConditionOperand(
            condition.substr(0, compare_position), nullptr);
        const std::string rhs = NormalizeRenderedConditionExpression(
            condition.substr(compare_position + compare_op.size()), nullptr);
        return lhs == expected_lhs && rhs == expected_rhs;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        std::size_t cursor = next_nonempty_noncomment(line_index + 1);
        if (cursor >= lines->size()) {
            continue;
        }

        bool had_while = false;
        std::size_t while_line = cursor;
        if (trim_line((*lines)[cursor]) == "while true do") {
            had_while = true;
            cursor = next_nonempty_noncomment(cursor + 1);
        }
        if (cursor >= lines->size()) {
            continue;
        }

        const std::size_t state0_line = cursor;
        std::string state0_condition;
        if (!parse_if_like_condition((*lines)[state0_line], &state0_condition)) {
            continue;
        }

        std::vector<std::string> state0_parts;
        if (!SplitRenderedTopLevelBooleanChain(state0_condition, "and", &state0_parts) ||
            state0_parts.size() < 2) {
            continue;
        }

        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(state0_parts.front(), &compare_position, &compare_op) ||
            compare_op != "==") {
            continue;
        }

        const std::string state_lhs = NormalizeRenderedConditionOperand(
            state0_parts.front().substr(0, compare_position), nullptr);
        const std::string state_rhs = NormalizeRenderedConditionExpression(
            state0_parts.front().substr(compare_position + compare_op.size()), nullptr);
        const std::string state0_nested_condition =
            join_boolean_parts(state0_parts, 1, state0_parts.size());
        if (state_lhs.empty() || state_rhs != "0" || state0_nested_condition.empty() ||
            state_lhs.find("CurrentState") == std::string::npos) {
            continue;
        }

        std::size_t state1_line = lines->size();
        for (std::size_t probe = state0_line + 1; probe < lines->size(); ++probe) {
            std::string condition;
            if (!parse_if_like_condition((*lines)[probe], &condition)) {
                continue;
            }
            if (condition_matches_state(condition, state_lhs, "1")) {
                state1_line = probe;
                break;
            }
        }
        if (state1_line >= lines->size()) {
            continue;
        }

        const std::size_t state1_outer_if_line = next_nonempty_noncomment(state1_line + 1);
        std::string state1_outer_if_condition;
        if (state1_outer_if_line >= lines->size() ||
            !TryExtractRenderedIfCondition((*lines)[state1_outer_if_line],
                                           &state1_outer_if_condition)) {
            continue;
        }

        std::size_t state2_line = lines->size();
        for (std::size_t probe = state1_outer_if_line + 1; probe < lines->size(); ++probe) {
            std::string condition;
            if (!parse_if_like_condition((*lines)[probe], &condition)) {
                continue;
            }
            if (condition_matches_state(condition, state_lhs, "2")) {
                state2_line = probe;
                break;
            }
        }
        if (state2_line >= lines->size()) {
            continue;
        }

        const std::size_t yield_line = next_nonempty_noncomment(state2_line + 1);
        const std::size_t jump_comment_line = next_nonempty(yield_line + 1);
        const std::size_t final_end_line = next_nonempty_noncomment(jump_comment_line + 1);
        if (yield_line >= lines->size() ||
            trim_line((*lines)[yield_line]) != "coroutine.yield()" ||
            jump_comment_line >= lines->size() ||
            TrimLuaWhitespace((*lines)[jump_comment_line]) !=
                "-- unsupported backward jump to pc 0" ||
            final_end_line >= lines->size() || trim_line((*lines)[final_end_line]) != "end") {
            continue;
        }

        const std::size_t state1_last_end = previous_nonempty_noncomment(state2_line);
        if (state1_last_end >= lines->size() || trim_line((*lines)[state1_last_end]) != "end") {
            continue;
        }

        std::size_t promoted_else_line = lines->size();
        for (std::size_t probe = state1_last_end; probe > state1_outer_if_line + 1; --probe) {
            const std::size_t current = probe - 1;
            if (trim_line((*lines)[current]) == "else") {
                promoted_else_line = current;
                break;
            }
        }
        if (promoted_else_line >= lines->size()) {
            continue;
        }

        const std::string while_indent =
            had_while ? std::string(GetRenderedLuaIndent((*lines)[while_line]))
                      : std::string(GetRenderedLuaIndent((*lines)[state0_line]));
        const std::string outer_indent = while_indent + "  ";
        const std::string state1_outer_if_indent(
            GetRenderedLuaIndent((*lines)[state1_outer_if_line]));
        const std::string promoted_else_indent(
            GetRenderedLuaIndent((*lines)[promoted_else_line]));
        if (state1_outer_if_indent.empty() || promoted_else_indent.empty() ||
            promoted_else_indent.size() <= state1_outer_if_indent.size()) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.push_back(while_indent + "while true do");
        replacement.push_back(outer_indent + "if " + state_lhs + " == 0 then");
        replacement.push_back(outer_indent + "  if " + state0_nested_condition + " then");
        if (!append_reindented_body(
                &replacement, state0_line + 1, state1_line, outer_indent + "    ")) {
            continue;
        }
        replacement.push_back(outer_indent + "  end");
        replacement.push_back(outer_indent + "elseif " + state_lhs + " == 1 then");
        if (!append_reindented_body(
                &replacement, state1_line + 1, promoted_else_line, outer_indent + "  ")) {
            continue;
        }
        replacement.push_back(promoted_else_indent + "end");
        replacement.push_back(state1_outer_if_indent + "else");
        if (!append_reindented_body(&replacement,
                                    promoted_else_line + 1,
                                    state1_last_end,
                                    state1_outer_if_indent + "  ")) {
            continue;
        }
        replacement.push_back(state1_outer_if_indent + "end");
        replacement.push_back(outer_indent + "elseif " + state_lhs + " == 2 then");
        replacement.push_back(outer_indent + "end");
        replacement.push_back(outer_indent + "coroutine.yield()");
        replacement.push_back(while_indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(final_end_line + 1));
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

bool SimplifyRenderedLuaUnhandledCurrentStateLeadingGuards(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct BranchSpan {
        std::string condition;
        std::size_t body_start = 0;
        std::size_t body_end = 0;
    };

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
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start > body_end || body_end > lines->size()) {
            return false;
        }
        if (body_start == body_end) {
            return true;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return true;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }

            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }

            if (stripped_trimmed.empty()) {
                continue;
            }

            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) + std::string(stripped_trimmed));
            }
        }
        return true;
    };
    const auto condition_matches_state = [&](std::string_view condition,
                                             std::string_view expected_lhs,
                                             std::string_view expected_rhs) {
        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(condition, &compare_position, &compare_op) ||
            compare_op != "==") {
            return false;
        }

        const std::string lhs = NormalizeRenderedConditionOperand(
            condition.substr(0, compare_position), nullptr);
        const std::string rhs = NormalizeRenderedConditionExpression(
            condition.substr(compare_position + compare_op.size()), nullptr);
        return lhs == expected_lhs && rhs == expected_rhs;
    };
    const auto join_boolean_parts = [](const std::vector<std::string>& parts,
                                       std::size_t begin,
                                       std::size_t end) -> std::string {
        if (begin >= end || end > parts.size()) {
            return {};
        }

        std::string expression = parts[begin];
        for (std::size_t index = begin + 1; index < end; ++index) {
            expression = RenderLogicalExpression(expression, "and", parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };
    const auto collect_simple_branch_chain = [&](const auto& self,
                                                 std::size_t if_line,
                                                 std::vector<BranchSpan>* branches,
                                                 std::size_t* chain_end) -> bool {
        if (branches == nullptr || chain_end == nullptr || if_line >= lines->size()) {
            return false;
        }

        std::string current_condition;
        if (!parse_if_like_condition((*lines)[if_line], &current_condition)) {
            return false;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
            return false;
        }

        std::size_t branch_body_start = if_line + 1;
        int depth = 1;
        for (std::size_t probe = if_line + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                if (probe <= branch_body_start) {
                    return false;
                }
                branches->push_back({current_condition, branch_body_start, probe});
                if (!parse_if_like_condition((*lines)[probe], &current_condition)) {
                    return false;
                }
                branch_body_start = probe + 1;
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                return false;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }

        if (branch_body_start >= block_end) {
            return false;
        }
        branches->push_back({current_condition, branch_body_start, block_end});
        *chain_end = block_end;
        return branches->size() >= 2;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        std::size_t cursor = next_nonempty_noncomment(line_index + 1);
        if (cursor >= lines->size()) {
            continue;
        }

        bool had_while = false;
        std::size_t while_line = cursor;
        if (trim_line((*lines)[cursor]) == "while true do") {
            had_while = true;
            cursor = next_nonempty_noncomment(cursor + 1);
        }
        if (cursor >= lines->size()) {
            continue;
        }

        std::vector<BranchSpan> branches;
        std::size_t chain_end = cursor;
        if (!collect_simple_branch_chain(collect_simple_branch_chain, cursor, &branches, &chain_end) ||
            branches.size() < 2) {
            continue;
        }

        std::vector<std::string> first_parts;
        if (!SplitRenderedTopLevelBooleanChain(branches.front().condition, "and", &first_parts) ||
            first_parts.size() < 2) {
            continue;
        }

        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(first_parts.front(), &compare_position, &compare_op) ||
            compare_op != "==") {
            continue;
        }

        const std::string state_lhs = NormalizeRenderedConditionOperand(
            first_parts.front().substr(0, compare_position), nullptr);
        const std::string state_rhs = NormalizeRenderedConditionExpression(
            first_parts.front().substr(compare_position + compare_op.size()), nullptr);
        const std::string leading_guard =
            join_boolean_parts(first_parts, 1, first_parts.size());
        if (state_lhs.empty() || state_rhs != "0" || leading_guard.empty() ||
            state_lhs.find("CurrentState") == std::string::npos) {
            continue;
        }

        bool later_states_valid = true;
        for (std::size_t branch_index = 1; branch_index < branches.size(); ++branch_index) {
            if (!(condition_matches_state(branches[branch_index].condition, state_lhs, "1") ||
                  condition_matches_state(branches[branch_index].condition, state_lhs, "2") ||
                  condition_matches_state(branches[branch_index].condition, state_lhs, "3") ||
                  condition_matches_state(branches[branch_index].condition, state_lhs, "4") ||
                  condition_matches_state(branches[branch_index].condition, state_lhs, "5") ||
                  condition_matches_state(branches[branch_index].condition, state_lhs, "6"))) {
                later_states_valid = false;
                break;
            }
        }
        if (!later_states_valid) {
            continue;
        }

        const std::string while_indent =
            had_while ? std::string(GetRenderedLuaIndent((*lines)[while_line])) : std::string();
        const std::string outer_indent =
            had_while ? while_indent + "  " : std::string(GetRenderedLuaIndent((*lines)[cursor]));

        std::vector<std::string> replacement;
        if (had_while) {
            replacement.push_back(while_indent + "while true do");
        }
        replacement.push_back(outer_indent + "if " + state_lhs + " == 0 then");
        replacement.push_back(outer_indent + "  if " + leading_guard + " then");
        if (!append_reindented_body(
                &replacement, branches.front().body_start, branches.front().body_end, outer_indent + "    ")) {
            continue;
        }
        replacement.push_back(outer_indent + "  end");
        for (std::size_t branch_index = 1; branch_index < branches.size(); ++branch_index) {
            replacement.push_back(outer_indent + "elseif " + branches[branch_index].condition + " then");
            if (!append_reindented_body(&replacement,
                                        branches[branch_index].body_start,
                                        branches[branch_index].body_end,
                                        outer_indent + "  ")) {
                replacement.clear();
                break;
            }
        }
        if (replacement.empty()) {
            continue;
        }
        replacement.push_back(outer_indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(chain_end + 1));
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

#include "rendered_lua_text_unhandled_current_state_cleanup.inc"
