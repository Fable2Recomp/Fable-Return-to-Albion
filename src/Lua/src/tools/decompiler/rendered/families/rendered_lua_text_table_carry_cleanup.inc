bool SimplifyRenderedLuaKnownFieldCarryAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    static const std::unordered_set<std::string> kAllowedFields = {
        "ActionModifiers",
        "Angles",
        "AntiSpamData",
        "BlockData",
        "DodgeAnimations",
        "Enrage",
        "EvadeAnimations",
        "EvadeShotsData",
        "FastWeaponChanges",
        "InstantFleeCreatureTypes",
        "LaughAtKnockdown",
        "Limits",
        "NonAttackableCreatureTypes",
        "Sequences",
        "ShootingBalanceData",
        "ValidStates",
    };

    const auto extract_trailing_field_name = [](std::string_view expression,
                                                std::string* field_name) -> bool {
        expression = TrimLuaWhitespace(expression);
        const std::size_t dot = expression.rfind('.');
        if (dot == std::string_view::npos || dot + 1 >= expression.size()) {
            return false;
        }

        const std::string_view member = expression.substr(dot + 1);
        if (!IsValidLuaIdentifier(member)) {
            return false;
        }

        if (field_name != nullptr) {
            *field_name = std::string(member);
        }
        return true;
    };

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaKnownFieldCarryAssignments";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.empty()) {
                continue;
            }

            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            std::vector<std::string> lhs_parts;
            std::vector<std::string> rhs_parts;
            if (!SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(0, equal)),
                                                 &lhs_parts) ||
                !SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(equal + 1)),
                                                 &rhs_parts) ||
                lhs_parts.size() != 2 || rhs_parts.size() != 2) {
                continue;
            }

            const std::string lhs0 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[0]), nullptr);
            const std::string lhs1 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[1]), nullptr);
            const std::string rhs0 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[0]), nullptr);
            const std::string rhs1 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[1]), nullptr);
            if (!IsRenderedSafePrefixExpression(lhs0) || !IsRenderTemporaryName(lhs1) ||
                rhs0 != lhs1 || rhs1.size() < 2 || rhs1.front() != '{' || rhs1.back() != '}') {
                continue;
            }

            std::string field_name;
            if (!extract_trailing_field_name(lhs0, &field_name) ||
                !kAllowedFields.contains(field_name)) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();
            const bool emit_local =
                ShouldEmitLocalForRenderedIdentifierTarget(*lines, line_index, lhs1);

            (*lines)[line_index] = indent + lhs0 + " = " + lhs1;
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                          indent + (emit_local ? "local " : std::string()) + lhs1 + " = " +
                              rhs1 + std::string(comment));
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaTopLevelObjectCarryAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    static const std::unordered_set<std::string> kKnownFieldTargets = {
        "ActionModifiers",
        "Angles",
        "AntiSpamData",
        "BlockData",
        "DodgeAnimations",
        "Enrage",
        "EvadeAnimations",
        "EvadeShotsData",
        "FastWeaponChanges",
        "InstantFleeCreatureTypes",
        "LaughAtKnockdown",
        "Limits",
        "NonAttackableCreatureTypes",
        "Sequences",
        "ShootingBalanceData",
        "ValidStates",
    };

    const auto extract_trailing_field_name = [](std::string_view expression,
                                                std::string* field_name) -> bool {
        expression = TrimLuaWhitespace(expression);
        const std::size_t dot = expression.rfind('.');
        if (dot == std::string_view::npos || dot + 1 >= expression.size()) {
            return false;
        }

        const std::string_view member = expression.substr(dot + 1);
        if (!IsValidLuaIdentifier(member)) {
            return false;
        }

        if (field_name != nullptr) {
            *field_name = std::string(member);
        }
        return true;
    };

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaTopLevelObjectCarryAssignments";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.empty()) {
                continue;
            }

            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            std::vector<std::string> lhs_parts;
            std::vector<std::string> rhs_parts;
            if (!SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(0, equal)),
                                                 &lhs_parts) ||
                !SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(equal + 1)),
                                                 &rhs_parts) ||
                lhs_parts.size() != 2 || rhs_parts.size() != 2) {
                continue;
            }

            const std::string lhs0 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[0]), nullptr);
            const std::string lhs1 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[1]), nullptr);
            const std::string rhs0 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[0]), nullptr);
            const std::string rhs1 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[1]), nullptr);
            if (IsRenderTemporaryName(lhs0) || !IsRenderedSafePrefixExpression(lhs0) ||
                !IsRenderTemporaryName(lhs1) || rhs0 != lhs1 || rhs1.size() < 2 ||
                rhs1.front() != '{' || rhs1.back() != '}') {
                continue;
            }

            std::string field_name;
            if (extract_trailing_field_name(lhs0, &field_name) &&
                kKnownFieldTargets.contains(field_name)) {
                continue;
            }

            if (CountIdentifierUses(lhs0, lhs1) != 0 || CountIdentifierUses(rhs1, lhs1) != 0) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();
            const bool emit_local =
                ShouldEmitLocalForRenderedIdentifierTarget(*lines, line_index, lhs1);

            (*lines)[line_index] =
                indent + (emit_local ? "local " : std::string()) + lhs1 + " = " + rhs1;
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                          indent + lhs0 + " = " + lhs1 + std::string(comment));
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}
