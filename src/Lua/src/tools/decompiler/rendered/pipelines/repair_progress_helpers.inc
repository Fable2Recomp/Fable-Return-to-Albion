bool IsUnclosedConstructValidationFamily(std::string_view raw_error) {
    const std::string summary = SummarizeSyntaxError(raw_error);
    return StartsWith(summary, "'end'_expected_(to_close_'") ||
           StartsWith(summary, "'until'_expected_");
}

bool IsBytecodeProvenValidationProgress(std::string_view baseline_error,
                                        std::string_view candidate_error,
                                        const LuaOutputQualitySummary& baseline_quality,
                                        const LuaOutputQualitySummary& candidate_quality) {
    if (baseline_error.empty() || candidate_error.empty()) {
        return false;
    }
    if (!IsUnclosedConstructValidationFamily(baseline_error) ||
        !IsUnclosedConstructValidationFamily(candidate_error)) {
        return false;
    }
    if (!IsLuaOutputQualityNoWorse(baseline_quality, candidate_quality)) {
        return false;
    }

    const std::size_t baseline_open_line = ExtractLuaOpenConstructLine(baseline_error);
    const std::size_t candidate_open_line = ExtractLuaOpenConstructLine(candidate_error);
    if (baseline_open_line != 0 && candidate_open_line != 0 &&
        baseline_open_line != candidate_open_line) {
        return true;
    }

    const refii::lua::internal::SyntaxErrorDescriptor baseline_descriptor =
        refii::lua::internal::DescribeSyntaxError(baseline_error);
    const refii::lua::internal::SyntaxErrorDescriptor candidate_descriptor =
        refii::lua::internal::DescribeSyntaxError(candidate_error);
    return baseline_descriptor.summary != candidate_descriptor.summary;
}

bool IsSameFamilyQualityProgress(std::string_view baseline_source,
                                 std::string_view baseline_error,
                                 const LuaOutputQualitySummary& baseline_quality,
                                 std::string_view candidate_source,
                                 std::string_view candidate_error,
                                 const LuaOutputQualitySummary& candidate_quality) {
    if (!IsLuaOutputQualityBetter(baseline_quality, candidate_quality)) {
        return false;
    }

    return BuildRepairFamilyStateKey(baseline_source, baseline_error) ==
           BuildRepairFamilyStateKey(candidate_source, candidate_error);
}
