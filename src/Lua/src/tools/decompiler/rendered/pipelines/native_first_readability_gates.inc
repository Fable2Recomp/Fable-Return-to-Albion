std::size_t ScoreLuaReadabilityRisk(const LuaReadabilitySummary& summary) {
    return summary.risk_score;
}

bool LooksLikeRegisterizedLua(const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 10) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count >= 3 &&
        summary.temporary_leading_line_count * 2 >= summary.nonempty_line_count) {
        return true;
    }

    if (summary.temporary_leading_line_count >= 8 &&
        summary.multi_temporary_token_line_count >= 6 &&
        ScoreLuaReadabilityRisk(summary) >= 48) {
        return true;
    }

    return summary.nonempty_line_count >= 24 &&
           summary.temporary_leading_line_count * 3 >= summary.nonempty_line_count &&
           summary.unique_temporary_identifier_count >= 4 &&
           ScoreLuaReadabilityRisk(summary) >= 64;
}

bool IsRunnableLuaDespiteTemporaryNoise(const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 200) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    return summary.temporary_leading_line_count * 5 < summary.nonempty_line_count &&
           summary.multi_temporary_token_line_count * 6 < summary.nonempty_line_count &&
           summary.temporary_local_declaration_count * 16 < summary.nonempty_line_count;
}

bool IsStructuredTableInitializerLuaDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 80) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0 ||
        summary.temporary_table_field_assignment_count < 24 ||
        summary.temporary_local_declaration_count * 24 >= summary.nonempty_line_count ||
        summary.multi_temporary_token_line_count * 4 >= summary.nonempty_line_count) {
        return false;
    }

    if (summary.temporary_table_field_assignment_count * 2 >=
        summary.temporary_leading_line_count) {
        return true;
    }

    return summary.nonempty_line_count >= 300 &&
           summary.temporary_assignment_count * 2 >=
               summary.temporary_leading_line_count &&
           summary.unique_temporary_identifier_count <= 12;
}

bool IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 240) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0 ||
        summary.unique_temporary_identifier_count > 24 ||
        summary.temporary_leading_line_count * 8 >= summary.nonempty_line_count * 5) {
        return false;
    }

    const bool table_heavy = summary.temporary_table_field_assignment_count >= 20;
    const bool assignment_heavy = summary.temporary_assignment_count * 3 >=
                                  summary.temporary_leading_line_count * 2;
    return table_heavy || assignment_heavy;
}

bool ShouldTryRawAlternativeForValidatedCandidate(std::string_view source) {
    const LuaReadabilitySummary summary = SummarizeLuaReadability(source);
    if (summary.suspicious_self_call_assignment_count != 0 ||
        summary.suspicious_temporary_rhs_assignment_count != 0) {
        return true;
    }
    return LooksLikeRegisterizedLua(summary) &&
           !IsRunnableLuaDespiteTemporaryNoise(summary) &&
           !IsStructuredTableInitializerLuaDespiteTemporaryNoise(summary) &&
           !IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(summary);
}

bool ShouldTrySeedRepairForValidatedCandidate(const LuaReadabilitySummary& clean_summary) {
    return clean_summary.suspicious_self_call_assignment_count != 0 ||
           clean_summary.temporary_self_reference_assignment_count != 0;
}

bool ShouldTryShadowRepairForValidatedCandidate(const LuaReadabilitySummary& clean_summary,
                                                const LuaReadabilitySummary& raw_summary) {
    return clean_summary.temporary_local_declaration_count != 0 ||
           clean_summary.temporary_identifier_count != 0 ||
           clean_summary.temporary_leading_line_count != 0 ||
           raw_summary.temporary_local_declaration_count >= 8;
}

bool ShouldTryExpandedAssignmentRepairForValidatedCandidate(
    const LuaReadabilitySummary& clean_summary) {
    return clean_summary.temporary_identifier_count != 0 ||
           clean_summary.temporary_leading_line_count != 0 ||
           clean_summary.temporary_local_declaration_count != 0 ||
           clean_summary.temporary_assignment_count != 0 ||
           clean_summary.temporary_table_field_assignment_count != 0 ||
           clean_summary.temporary_self_reference_assignment_count != 0 ||
           clean_summary.suspicious_self_call_assignment_count != 0 ||
           clean_summary.suspicious_temporary_rhs_assignment_count != 0 ||
           clean_summary.obvious_nil_call_count != 0;
}

std::size_t CountUnresolvedStatementTempOwnershipPatterns(std::string_view source) {
    std::size_t count = 0;
    const std::vector<std::string> lines = SplitRenderedLines(source);
    for (const std::string& raw_line : lines) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(raw_line, &is_local, &lhs, &rhs)) {
            continue;
        }

        const std::string normalized_lhs =
            std::string(TrimLuaWhitespace(lhs));
        if (normalized_lhs.empty() ||
            !IsValidLuaIdentifier(normalized_lhs) ||
            IsLuaKeyword(normalized_lhs) ||
            IsRenderTemporaryName(normalized_lhs)) {
            continue;
        }

        bool has_temporary_token = false;
        ForEachLuaToken(rhs, [&](std::string_view token) {
            if (IsRenderTemporaryName(token)) {
                has_temporary_token = true;
            }
        });
        if (!has_temporary_token) {
            continue;
        }

        const std::string_view normalized_rhs = TrimLuaWhitespace(rhs);
        if (normalized_rhs.find(" and ") != std::string_view::npos ||
            normalized_rhs.find(" or ") != std::string_view::npos ||
            normalized_rhs.find("function()") != std::string_view::npos ||
            !is_local) {
            ++count;
        }
    }
    return count;
}

std::size_t CountNativeStatementTempCommits(std::string_view source) {
    std::size_t count = 0;
    const std::vector<std::string> lines = SplitRenderedLines(source);
    for (const std::string& raw_line : lines) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(raw_line, &is_local, &lhs, &rhs) ||
            !is_local) {
            continue;
        }

        const std::string normalized_lhs =
            std::string(TrimLuaWhitespace(lhs));
        if (normalized_lhs.empty() ||
            !IsValidLuaIdentifier(normalized_lhs) ||
            IsRenderTemporaryName(normalized_lhs)) {
            continue;
        }

        const std::string_view normalized_rhs = TrimLuaWhitespace(rhs);
        if (normalized_rhs.find(" and ") == std::string_view::npos &&
            normalized_rhs.find(" or ") == std::string_view::npos) {
            continue;
        }

        bool has_temporary_token = false;
        ForEachLuaToken(rhs, [&](std::string_view token) {
            if (IsRenderTemporaryName(token)) {
                has_temporary_token = true;
            }
        });
        if (has_temporary_token) {
            ++count;
        }
    }
    return count;
}

bool ShouldTryStatementTempsFromRawForValidatedCandidate(
    std::string_view source,
    const LuaReadabilitySummary& clean_summary) {
    if (!ShouldTryExpandedAssignmentRepairForValidatedCandidate(clean_summary)) {
        return false;
    }

    // Keep statement-temp raw repair tightly scoped to unresolved ownership
    // invariants that survive native reconstruction and tiny rendered cleanup.
    const std::size_t unresolved_patterns =
        CountUnresolvedStatementTempOwnershipPatterns(source);
    if (unresolved_patterns == 0) {
        return false;
    }

    const std::size_t rough_line_count =
        static_cast<std::size_t>(std::count(source.begin(), source.end(), '\n')) +
        std::size_t{1};
    constexpr std::size_t kStatementTempRawRepairLineBudget = 800;
    if (rough_line_count > kStatementTempRawRepairLineBudget) {
        return false;
    }

    // If native reconstruction already commits statement-temp ownership locally,
    // prefer keeping the native result and avoid raw fallback churn.
    if (CountNativeStatementTempCommits(source) != 0) {
        return false;
    }

    return unresolved_patterns >= 4;
}

bool ShouldTryGuardedCarryRepairForValidatedCandidate(const LuaReadabilitySummary& raw_summary) {
    return raw_summary.temporary_assignment_count != 0 ||
           raw_summary.temporary_identifier_count != 0;
}

void AppendNotePart(std::string* note, std::string_view part) {
    if (note == nullptr || part.empty()) {
        return;
    }
    if (!note->empty()) {
        note->push_back(';');
    }
    note->append(part);
}
