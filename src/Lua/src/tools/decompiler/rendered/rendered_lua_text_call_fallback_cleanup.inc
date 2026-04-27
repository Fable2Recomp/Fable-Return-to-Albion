bool SimplifyRenderedLuaCallResultFallbackAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string assign_rhs;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &assign_is_local,
                                         &lhs_names,
                                         &assign_rhs) ||
            lhs_names.size() < 2) {
            continue;
        }

        std::vector<std::string> targets;
        std::vector<std::size_t> target_assignment_lines;
        std::size_t assigned_count = 0;
        std::size_t cursor = line_index + 1;
        if (assign_is_local) {
            bool all_temps = true;
            for (const std::string& temp_name : lhs_names) {
                if (!IsRenderTemporaryName(temp_name)) {
                    all_temps = false;
                    break;
                }
            }
            if (!all_temps) {
                continue;
            }

            targets.assign(lhs_names.size(), std::string());
            target_assignment_lines.assign(lhs_names.size(), 0);
            for (; cursor < lines->size(); ++cursor) {
                bool copy_is_local = false;
                std::string copy_lhs;
                std::string copy_rhs;
                if (!ParseRenderedSingleAssignmentLine((*lines)[cursor],
                                                       &copy_is_local,
                                                       &copy_lhs,
                                                       &copy_rhs) ||
                    copy_is_local) {
                    break;
                }

                const std::string normalized_rhs = StripRenderedWholeParentheses(
                    TrimLuaWhitespace(copy_rhs),
                    nullptr);
                const auto temp_it =
                    std::find(lhs_names.begin(), lhs_names.end(), normalized_rhs);
                if (temp_it == lhs_names.end()) {
                    break;
                }

                const std::size_t temp_index =
                    static_cast<std::size_t>(std::distance(lhs_names.begin(), temp_it));
                if (!targets[temp_index].empty()) {
                    assigned_count = 0;
                    break;
                }

                targets[temp_index] =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(copy_lhs), nullptr);
                target_assignment_lines[temp_index] = cursor;
                ++assigned_count;
            }

            if (assigned_count != lhs_names.size() || cursor >= lines->size()) {
                continue;
            }
        } else {
            targets.reserve(lhs_names.size());
            target_assignment_lines.reserve(lhs_names.size());
            bool valid_direct_targets = true;
            for (const std::string& lhs_name : lhs_names) {
                const std::string normalized_lhs = StripRenderedWholeParentheses(
                    TrimLuaWhitespace(lhs_name),
                    nullptr);
                if (normalized_lhs.empty() ||
                    !IsRenderedSafePrefixExpression(normalized_lhs)) {
                    valid_direct_targets = false;
                    break;
                }
                targets.push_back(normalized_lhs);
                target_assignment_lines.push_back(line_index);
            }
            if (!valid_direct_targets) {
                continue;
            }
            assigned_count = targets.size();
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[cursor],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local) {
            continue;
        }

        const std::string normalized_fallback_lhs = StripRenderedWholeParentheses(
            TrimLuaWhitespace(fallback_lhs),
            nullptr);
        const auto target_it =
            std::find(targets.begin(), targets.end(), normalized_fallback_lhs);
        if (target_it == targets.end()) {
            continue;
        }

        const std::string normalized_fallback_rhs = StripRenderedWholeParentheses(
            TrimLuaWhitespace(fallback_rhs),
            nullptr);
        if (normalized_fallback_rhs.empty() ||
            !IsRenderedSafePrefixExpression(normalized_fallback_rhs) ||
            CountIdentifierUses(normalized_fallback_rhs, normalized_fallback_lhs) != 0) {
            continue;
        }

        const std::size_t target_index =
            static_cast<std::size_t>(std::distance(targets.begin(), target_it));
        bool used_before_fallback = false;
        for (std::size_t probe = target_assignment_lines[target_index] + 1; probe < cursor;
             ++probe) {
            if (CountIdentifierUses((*lines)[probe], normalized_fallback_lhs) != 0) {
                used_before_fallback = true;
                break;
            }
        }
        if (used_before_fallback) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[cursor]));
        (*lines)[cursor] = indent + normalized_fallback_lhs + " = (" +
                           normalized_fallback_rhs + " or " +
                           normalized_fallback_lhs + ")";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedCallFallbackAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs)) {
            continue;
        }

        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index + 1], &condition) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local ||
            fallback_lhs != assign_lhs) {
            continue;
        }

        std::string positive_expression;
        if (!TryBuildRenderedPositiveFallbackExpression(condition, &positive_expression)) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine(assign_rhs, &callee, &arguments) || arguments.empty() ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(arguments.front()), nullptr) !=
                "nil") {
            continue;
        }

        arguments.front() = RenderLogicalExpression(
            positive_expression,
            "or",
            std::string(TrimLuaWhitespace(fallback_rhs)));
        std::ostringstream rebuilt_rhs;
        rebuilt_rhs << callee << "(";
        for (std::size_t arg_index = 0; arg_index < arguments.size(); ++arg_index) {
            if (arg_index != 0) {
                rebuilt_rhs << ", ";
            }
            rebuilt_rhs << arguments[arg_index];
        }
        rebuilt_rhs << ")";

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + (assign_is_local ? "local " : "") + assign_lhs +
                               " = " + rebuilt_rhs.str();
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedTemporaryFallbackAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local) {
            continue;
        }

        bool overwrite_is_local = false;
        std::string overwrite_lhs;
        std::string overwrite_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 3],
                                               &overwrite_is_local,
                                               &overwrite_lhs,
                                               &overwrite_rhs) ||
            overwrite_is_local ||
            overwrite_lhs != fallback_lhs ||
            !IsRenderTemporaryName(
                StripRenderedWholeParentheses(TrimLuaWhitespace(overwrite_rhs), nullptr))) {
            continue;
        }

        std::string positive_expression;
        if (!TryBuildRenderedPositiveFallbackExpression(condition, &positive_expression)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + fallback_lhs + " = " +
                               RenderLogicalExpression(
                                   positive_expression,
                                   "or",
                                   std::string(TrimLuaWhitespace(fallback_rhs)));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedBranchLocalFallbackAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        bool branch_is_local = false;
        std::string branch_lhs;
        std::string branch_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &branch_is_local,
                                               &branch_lhs,
                                               &branch_rhs) ||
            !branch_is_local || !IsRenderTemporaryName(branch_lhs)) {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 3],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            IsRenderTemporaryName(fallback_lhs)) {
            continue;
        }

        const std::string normalized_condition =
            NormalizeRenderedConditionExpression(condition, nullptr);
        const std::string normalized_branch_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(branch_rhs), nullptr);
        const std::string normalized_fallback_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(fallback_rhs), nullptr);
        if (normalized_condition.empty() || normalized_branch_rhs.empty() ||
            normalized_fallback_rhs.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + (fallback_is_local ? "local " : "") + fallback_lhs +
                               " = " +
                               RenderLogicalExpression(
                                   RenderLogicalExpression(normalized_condition,
                                                           "and",
                                                           normalized_branch_rhs),
                                   "or",
                                   normalized_fallback_rhs);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaNestedGuardedLocalFallbackAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        std::vector<std::string> conditions;
        std::size_t cursor = line_index;
        for (;;) {
            std::string condition;
            if (!TryExtractRenderedIfCondition((*lines)[cursor], &condition)) {
                break;
            }
            condition = NormalizeRenderedConditionExpression(condition, nullptr);
            if (condition.empty()) {
                break;
            }
            conditions.push_back(std::move(condition));
            ++cursor;
        }
        if (conditions.empty()) {
            continue;
        }

        bool branch_is_local = false;
        std::string branch_lhs;
        std::string branch_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[cursor],
                                               &branch_is_local,
                                               &branch_lhs,
                                               &branch_rhs) ||
            !branch_is_local || branch_lhs.empty()) {
            continue;
        }
        ++cursor;

        std::string effective_branch_rhs = std::string(TrimLuaWhitespace(branch_rhs));
        if (cursor + 2 < lines->size() &&
            TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor])) ==
                ("if not " + branch_lhs + " then")) {
            bool nested_fallback_is_local = false;
            std::string nested_fallback_lhs;
            std::string nested_fallback_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[cursor + 1],
                                                   &nested_fallback_is_local,
                                                   &nested_fallback_lhs,
                                                   &nested_fallback_rhs) ||
                nested_fallback_is_local ||
                nested_fallback_lhs != branch_lhs ||
                TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor + 2])) != "end") {
                continue;
            }
            effective_branch_rhs = RenderLogicalExpression(
                effective_branch_rhs,
                "or",
                std::string(TrimLuaWhitespace(nested_fallback_rhs)));
            cursor += 3;
        }

        for (std::size_t depth = 0; depth < conditions.size(); ++depth) {
            if (cursor >= lines->size() ||
                TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor])) != "end") {
                conditions.clear();
                break;
            }
            ++cursor;
        }
        if (conditions.empty() || cursor >= lines->size()) {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[cursor],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            !fallback_is_local ||
            fallback_lhs != branch_lhs) {
            continue;
        }

        std::string combined_condition = conditions.front();
        for (std::size_t i = 1; i < conditions.size(); ++i) {
            combined_condition =
                RenderLogicalExpression(combined_condition, "and", conditions[i]);
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + "local " + fallback_lhs + " = " +
            RenderLogicalExpression(
                RenderLogicalExpression(combined_condition, "and", effective_branch_rhs),
                "or",
                std::string(TrimLuaWhitespace(fallback_rhs)));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(cursor + 1));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedTruthyLiteralFallbackAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs)) {
            continue;
        }

        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index + 1], &condition) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local ||
            fallback_lhs != seed_lhs ||
            TrimLuaWhitespace(condition) != ("not " + seed_lhs)) {
            continue;
        }

        std::vector<std::string> seed_parts;
        if (!SplitRenderedTopLevelBooleanChain(seed_rhs, "or", &seed_parts) ||
            seed_parts.size() != 2) {
            continue;
        }

        const std::string condition_expression = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(seed_parts[0]),
            nullptr);
        const std::string_view literal_part = TrimLuaWhitespace(seed_parts[1]);
        const std::string literal_expression = NormalizeRenderedConditionOperand(
            StartsWithLuaKeyword(literal_part, "not")
                ? TrimLuaWhitespace(literal_part.substr(3))
                : std::string_view{},
            nullptr);
        if (condition_expression.empty() || !StartsWithLuaKeyword(literal_part, "not") ||
            !IsRenderedDefinitelyTruthyExpression(literal_expression)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + (seed_is_local ? "local " : "") + seed_lhs + " = " +
                               RenderLogicalExpression(
                                   RenderLogicalExpression(condition_expression,
                                                           "and",
                                                           literal_expression),
                                   "or",
                                   std::string(TrimLuaWhitespace(fallback_rhs)));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
    }

    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local) {
            continue;
        }

        std::vector<std::string> condition_parts;
        if (!SplitRenderedTopLevelBooleanChain(condition, "or", &condition_parts) ||
            condition_parts.size() != 2 ||
            !StartsWithLuaKeyword(TrimLuaWhitespace(condition_parts[0]), "not") ||
            !StartsWithLuaKeyword(TrimLuaWhitespace(condition_parts[1]), "not")) {
            continue;
        }

        const std::string_view first_part = TrimLuaWhitespace(condition_parts[0]);
        const std::string_view second_part = TrimLuaWhitespace(condition_parts[1]);
        const std::string condition_expression = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(first_part.substr(3)),
            nullptr);
        const std::string literal_expression = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(second_part.substr(3)),
            nullptr);
        if (condition_expression.empty() ||
            !IsRenderedDefinitelyTruthyExpression(literal_expression)) {
            continue;
        }

        const bool emit_local =
            ShouldEmitLocalForRenderedIdentifierTarget(*lines, line_index, fallback_lhs);
        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + (emit_local ? "local " : "") + fallback_lhs +
                               " = " +
                               RenderLogicalExpression(
                                   RenderLogicalExpression(condition_expression,
                                                           "and",
                                                           literal_expression),
                                   "or",
                                   std::string(TrimLuaWhitespace(fallback_rhs)));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
    }

    return changed;
}
