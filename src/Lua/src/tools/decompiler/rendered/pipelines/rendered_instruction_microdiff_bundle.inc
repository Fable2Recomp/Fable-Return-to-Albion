bool ApplyRenderedLuaSharedInstructionMicroDiffTail(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    changed |= refii::lua::SimplifyRenderedLuaExplicitSelfMethodArguments(lines);
    changed |= refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments(lines);
    changed |= refii::lua::SimplifyRenderedLuaMethodArg0SelfParameters(lines);
    changed |= TrySwapRenderedReturnSelfRightHandComparisonsForParity(lines);
    changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedCallFieldGuardsForParity(lines);
    changed |= refii::lua::NormalizeRenderedLuaMeterPercentLocalForParity(lines);
    changed |= refii::lua::NormalizeRenderedLuaDebugDrawTextZOffsetForParity(lines);
    return changed;
}

bool TryApplyRenderedLuaSeedReturnDebugMicroDiffBundle(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    changed |= refii::lua::ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaInlineLocalTableSeedFieldsForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaReturnThenFallbackElseForParity(lines);
    changed |= refii::lua::NormalizeRenderedLuaDebugDrawTextZOffsetForParity(lines);
    return changed;
}

bool TryApplyRenderedLuaNearExactInstructionMicroDiffCleanup(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    bool changed = false;
    changed |= ApplyRenderedLuaSharedInstructionMicroDiffCore(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateSingleUseGuardTemps(lines);
    changed |= refii::lua::SimplifyRenderedLuaNestedAssignedGuardChains(lines);
    changed |= refii::lua::SimplifyRenderedLuaNearbyAssignedRepeatedConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaAdjacentDuplicateCallValueConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateAssignedConditionExpressionUses(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedLocalCallPropertyUsesForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedLocalCallExpressionUsesForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedLocalExpressionUsesForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaScopedMemberAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelMemberAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelFunctionAliasAssignments(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelImmediateResultAliases(lines);
    changed |= refii::lua::ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaInlineLocalTableSeedFieldsForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaReturnThenFallbackElseForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaOrTableFallbackAssignmentsForParity(lines);
    changed |= refii::lua::NormalizeRenderedLuaFreshTargetTableSeeds(lines);
    changed |= refii::lua::NormalizeRenderedLuaStaleTargetTableAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaStaleTargetLiteralOverwrites(lines);
    changed |= refii::lua::SimplifyRenderedLuaTableBuilders(lines);
    changed |= refii::lua::SimplifyRenderedLuaLocalTableBuilderCallArguments(lines);
    changed |= refii::lua::SimplifyRenderedLuaNestedGuardedDuplicateBodies(lines);
    changed |= refii::lua::SimplifyRenderedLuaNestedGuardedFallbackDuplicateBodies(lines);
    changed |= ApplyRenderedLuaSharedInstructionMicroDiffTail(lines);
    return changed;
}

bool TryApplyRenderedLuaTinyInstructionMicroDiffCleanup(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    bool changed = false;
    changed |= ApplyRenderedLuaSharedInstructionMicroDiffCore(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelImmediateResultAliases(lines);
    changed |= refii::lua::ExpandRenderedLuaPredicateBooleanReturnsForParity(lines);
    changed |= ApplyRenderedLuaSharedInstructionMicroDiffTail(lines);
    return changed;
}

bool NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(std::vector<std::string>* lines);

bool TryApplyRenderedLuaNearExactInstructionPolarityCleanup(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    bool changed = false;
    changed |= refii::lua::SimplifyRenderedLuaConditionHeaders(lines);
    changed |= refii::lua::SimplifyRenderedLuaRedundantGuardConditionParentheses(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanAssertConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanCoercionSelections(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanCoercionReturns(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanLoadPairs(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanCarryConditions(lines);
    changed |= NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(lines);
    changed |= TrySwapRenderedReturnSelfRightHandComparisonsForParity(lines);
    changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(lines);
    return changed;
}

bool TryInlineRenderedLuaImmediateSingleUseIndexReturnLocals(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                     &is_local,
                                                     &lhs_names,
                                                     &rhs_expression) ||
            !is_local || lhs_names.size() != 1) {
            continue;
        }

        const std::string local_name =
            std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
        const std::string rhs_trimmed =
            std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
        if (!refii::lua::IsValidLuaIdentifier(local_name) || rhs_trimmed.empty() ||
            rhs_trimmed.find('\n') != std::string::npos ||
            rhs_trimmed.find(',') != std::string::npos) {
            continue;
        }

        std::size_t return_line = line_index + 1;
        while (return_line < lines->size()) {
            const std::string_view between_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[return_line]));
            if (!between_trimmed.empty()) {
                break;
            }
            ++return_line;
        }
        if (return_line >= lines->size()) {
            continue;
        }

        const std::string_view return_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[return_line]));
        if (!return_trimmed.starts_with("return ")) {
            continue;
        }

        const std::string return_expression = std::string(
            refii::lua::TrimLuaWhitespace(return_trimmed.substr(7)));
        if (return_expression.empty() ||
            return_expression.find("[" + local_name + "]") == std::string::npos) {
            continue;
        }

        const auto is_identifier_start = [](char ch) {
            return ch == '_' ||
                   (ch >= 'A' && ch <= 'Z') ||
                   (ch >= 'a' && ch <= 'z');
        };
        const auto is_identifier_char = [&](char ch) {
            return is_identifier_start(ch) || (ch >= '0' && ch <= '9');
        };

        std::size_t local_name_token_count = 0;
        for (std::size_t cursor = 0; cursor < return_expression.size();) {
            if (!is_identifier_start(return_expression[cursor])) {
                ++cursor;
                continue;
            }
            std::size_t end = cursor + 1;
            while (end < return_expression.size() &&
                   is_identifier_char(return_expression[end])) {
                ++end;
            }
            if (std::string_view(return_expression.data() + cursor, end - cursor) ==
                local_name) {
                ++local_name_token_count;
            }
            cursor = end;
        }
        if (local_name_token_count != 1) {
            continue;
        }

        const std::string replacement_expression = "(" + rhs_trimmed + ")";
        std::string rewritten_expression;
        for (std::size_t cursor = 0; cursor < return_expression.size();) {
            if (!is_identifier_start(return_expression[cursor])) {
                rewritten_expression.push_back(return_expression[cursor]);
                ++cursor;
                continue;
            }
            std::size_t end = cursor + 1;
            while (end < return_expression.size() &&
                   is_identifier_char(return_expression[end])) {
                ++end;
            }
            if (std::string_view(return_expression.data() + cursor, end - cursor) ==
                local_name) {
                rewritten_expression += replacement_expression;
            } else {
                rewritten_expression.append(return_expression.substr(cursor, end - cursor));
            }
            cursor = end;
        }
        if (rewritten_expression.empty()) {
            continue;
        }

        const std::string rewritten_line =
            GetLuaLineIndent((*lines)[return_line]) + "return " + rewritten_expression;
        if (rewritten_line == (*lines)[return_line]) {
            continue;
        }

        (*lines)[line_index].clear();
        (*lines)[return_line] = rewritten_line;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_guard_line = [](std::string_view line,
                                     std::string* prefix_out,
                                     std::string* condition_out,
                                     std::string* suffix_out) {
        if (prefix_out == nullptr || condition_out == nullptr || suffix_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *prefix_out = "if ";
            *condition_out = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            *suffix_out = " then";
            return true;
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *prefix_out = "elseif ";
            *condition_out = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            *suffix_out = " then";
            return true;
        }
        if (trimmed.starts_with("while ") && trimmed.ends_with(" do")) {
            *prefix_out = "while ";
            *condition_out = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9)));
            *suffix_out = " do";
            return true;
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string guard_prefix;
        std::string guard_condition;
        std::string guard_suffix;
        if (!parse_guard_line((*lines)[line_index],
                              &guard_prefix,
                              &guard_condition,
                              &guard_suffix)) {
            continue;
        }

        const auto strip_outer_parentheses = [](std::string value) {
            value = std::string(refii::lua::TrimLuaWhitespace(value));
            while (value.size() >= 2 &&
                   value.front() == '(' &&
                   value.back() == ')') {
                value = std::string(refii::lua::TrimLuaWhitespace(
                    std::string_view(value).substr(1, value.size() - 2)));
            }
            return value;
        };

        const std::size_t comparator_position = guard_condition.find(" == ");
        if (comparator_position == std::string::npos ||
            guard_condition.find(" == ", comparator_position + 4) != std::string::npos ||
            guard_condition.find("~=") != std::string::npos ||
            guard_condition.find("<=") != std::string::npos ||
            guard_condition.find(">=") != std::string::npos ||
            guard_condition.find('<') != std::string::npos ||
            guard_condition.find('>') != std::string::npos) {
            continue;
        }

        const std::string lhs = strip_outer_parentheses(
            std::string(guard_condition.substr(0, comparator_position)));
        const std::string rhs = strip_outer_parentheses(
            std::string(guard_condition.substr(comparator_position + 4)));
        if (lhs.empty() || lhs != rhs || !refii::lua::IsValidLuaIdentifier(lhs)) {
            continue;
        }

        std::string alias_source;
        std::size_t looked_back = 0;
        for (std::size_t cursor = line_index; cursor > 0 && looked_back < 4; --cursor) {
            const std::size_t probe = cursor - 1;
            const std::string_view trimmed_probe = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (trimmed_probe.empty()) {
                continue;
            }
            ++looked_back;
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string assign_rhs;
            if (!refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                         &is_local,
                                                         &lhs_names,
                                                         &assign_rhs) ||
                !is_local ||
                lhs_names.size() != 1 ||
                std::string(refii::lua::TrimLuaWhitespace(lhs_names.front())) != lhs) {
                continue;
            }

            const std::string normalized_rhs =
                strip_outer_parentheses(std::string(assign_rhs));
            if (normalized_rhs.empty() ||
                normalized_rhs == lhs ||
                refii::lua::CountIdentifierUses(normalized_rhs, lhs) != 0 ||
                normalized_rhs.find(',') != std::string::npos ||
                normalized_rhs.find(" and ") != std::string::npos ||
                normalized_rhs.find(" or ") != std::string::npos) {
                continue;
            }
            alias_source = normalized_rhs;
            break;
        }
        if (alias_source.empty()) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] =
            indent + guard_prefix + lhs + " == " + alias_source + guard_suffix;
        changed = true;
    }

    return changed;
}

bool TryApplyRenderedLuaNearExactLocalTimingCleanup(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    bool changed = false;
    changed |= refii::lua::LocalizeRenderedLuaFirstAssignments(lines);
    changed |= refii::lua::LocalizeRenderedLuaFunctionLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaBranchShadowedLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaRedundantLocalRedeclarations(lines);
    changed |= refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries(lines);
    changed |= refii::lua::SimplifyRenderedLuaScopedMemberAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelMemberAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelFunctionAliasAssignments(lines);
    changed |= refii::lua::SimplifyRenderedLuaEscapingGuardedLocalDeclarations(lines);
    changed |= NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(lines);
    changed |= TryInlineRenderedLuaImmediateSingleUseIndexReturnLocals(lines);
    return changed;
}

bool ApplyRenderedLuaNearExactInstructionCleanupBundle(
    std::vector<std::string>* lines,
    bool include_local_timing,
    bool include_tiny_microdiff,
    bool include_polarity_cleanup) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    if (include_local_timing) {
        changed |= TryApplyRenderedLuaNearExactLocalTimingCleanup(lines);
    }
    if (include_tiny_microdiff) {
        changed |= TryApplyRenderedLuaTinyInstructionMicroDiffCleanup(lines);
    }
    changed |= TryApplyRenderedLuaNearExactInstructionMicroDiffCleanup(lines);
    if (include_polarity_cleanup) {
        changed |= TryApplyRenderedLuaNearExactInstructionPolarityCleanup(lines);
    }
    return changed;
}
