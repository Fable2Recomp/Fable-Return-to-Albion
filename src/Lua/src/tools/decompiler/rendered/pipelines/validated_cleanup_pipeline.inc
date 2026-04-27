struct ValidatedRenderedCleanupPipelineOptions {
    bool include_dead_temporary_alias_cleanup = false;
    bool include_namespace_alias_cleanup = false;
    bool include_ordered_compare_self_cleanup_tail = false;
    bool include_nested_duplicate_return_guard_cleanup = false;
    bool include_repeated_multi_return_predicate_cleanup = false;
};

struct RenderedLuaNonexactLightCleanupResult {
    bool assigned_functions_changed = false;
    bool branch_shadow_local_carries_changed = false;
    bool call_reassignment_self_compare_changed = false;
    bool explicit_self_method_arguments_changed = false;
    bool redundant_colon_self_arguments_changed = false;
    bool adjacent_duplicate_call_value_conditions_changed = false;
    bool adjacent_duplicate_local_calls_changed = false;
    bool straight_line_single_use_temps_changed = false;
    bool integral_float_literals_changed = false;

    [[nodiscard]] bool changed_any() const {
        return assigned_functions_changed || branch_shadow_local_carries_changed ||
               call_reassignment_self_compare_changed ||
               explicit_self_method_arguments_changed ||
               redundant_colon_self_arguments_changed ||
               adjacent_duplicate_call_value_conditions_changed ||
               adjacent_duplicate_local_calls_changed ||
               straight_line_single_use_temps_changed || integral_float_literals_changed;
    }
};

RenderedLuaNonexactLightCleanupResult ApplyRenderedLuaNonexactLightCleanup(
    std::vector<std::string>* lines,
    bool allow_call_reassignment_self_compare_cleanup) {
    RenderedLuaNonexactLightCleanupResult result;
    if (lines == nullptr) {
        return result;
    }

    result.assigned_functions_changed =
        refii::lua::SimplifyRenderedLuaAssignedFunctionDeclarations(lines);
    result.branch_shadow_local_carries_changed =
        refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries(lines);
    result.call_reassignment_self_compare_changed =
        allow_call_reassignment_self_compare_cleanup &&
        refii::lua::SimplifyRenderedLuaCallReassignmentSelfCompareGuards(lines);
    result.explicit_self_method_arguments_changed =
        refii::lua::SimplifyRenderedLuaExplicitSelfMethodArguments(lines);
    result.redundant_colon_self_arguments_changed =
        refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments(lines);
    result.adjacent_duplicate_call_value_conditions_changed =
        refii::lua::SimplifyRenderedLuaAdjacentDuplicateCallValueConditions(lines);
    result.adjacent_duplicate_local_calls_changed =
        refii::lua::SimplifyRenderedLuaAdjacentDuplicateLocalCallDeclarations(lines);
    result.straight_line_single_use_temps_changed =
        refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps(lines);
    result.integral_float_literals_changed =
        refii::lua::SimplifyRenderedLuaIntegralFloatLiterals(lines);
    return result;
}

void AppendRenderedLuaNonexactLightCleanupNotes(
    const RenderedLuaNonexactLightCleanupResult& cleanup_result,
    std::string_view note_prefix,
    std::string* note) {
    if (note == nullptr || note_prefix.empty() || !cleanup_result.changed_any()) {
        return;
    }

    *note = JoinNotes(*note,
                      std::string(note_prefix) +
                          "_validated_nonexact_lightcleanup_repaired");
    if (cleanup_result.assigned_functions_changed) {
        *note = JoinNotes(*note,
                          std::string(note_prefix) +
                              "_validated_nonexact_assignedfunctions_repaired");
    }
    if (cleanup_result.branch_shadow_local_carries_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_branchshadowlocalcarries_repaired");
    }
    if (cleanup_result.call_reassignment_self_compare_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_callreassignment_selfcompare_repaired");
    }
    if (cleanup_result.explicit_self_method_arguments_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_explicitselfcalls_repaired");
    }
    if (cleanup_result.redundant_colon_self_arguments_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_redundantcolonself_repaired");
    }
    if (cleanup_result.adjacent_duplicate_call_value_conditions_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_duplicatecallvalueconditions_repaired");
    }
    if (cleanup_result.adjacent_duplicate_local_calls_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_duplicatelocalcalls_repaired");
    }
    if (cleanup_result.straight_line_single_use_temps_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_singleusetemps_repaired");
    }
    if (cleanup_result.integral_float_literals_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_integralfloatliterals_repaired");
    }
}

template <typename KeepCandidateFn>
std::string ApplyValidatedRenderedCleanupPipeline(
    std::string source_text,
    const ValidatedRenderedCleanupPipelineOptions& options,
    KeepCandidateFn&& keep_candidate,
    bool* changed_any) {
    refii::lua::internal::LuaMutableTextContext cleanup_context(std::move(source_text));
    const std::string original_source = std::string(cleanup_context.EnsureSource());
    std::vector<std::string>* normalized_lines = cleanup_context.EnsureMutableLines();
    const bool had_trailing_newline = cleanup_context.HadTrailingNewline();
    std::string current_source = original_source;
    bool cleanup_changed = false;
    std::unordered_set<std::string> seen_sources;
    seen_sources.insert(current_source);

    const auto try_keep_cleanup_change = [&](auto step) {
        std::vector<std::string> candidate_lines = *normalized_lines;
        if (!step(&candidate_lines)) {
            return;
        }

        const std::string candidate_source = JoinLines(candidate_lines, had_trailing_newline);
        if (candidate_source == current_source ||
            seen_sources.find(candidate_source) != seen_sources.end()) {
            return;
        }

        if (!keep_candidate(candidate_source)) {
            return;
        }

        *normalized_lines = std::move(candidate_lines);
        cleanup_context.MarkLinesMutated();
        current_source = std::string(cleanup_context.EnsureSource());
        seen_sources.insert(current_source);
        cleanup_changed = true;
    };

    const std::array<RenderedLuaCleanupStepFn, 8> pre_cleanup_steps = {
        ::TryLocalizeTopLevelTemporaryAssignments,
        refii::lua::SimplifyRenderedLuaLoopTrailingReturnElse,
        refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals,
        refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals,
        refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals,
        refii::lua::SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals,
        refii::lua::SimplifyRenderedLuaRedundantIpairsTableReseeds,
        refii::lua::ComposeRenderedLuaValidatedMemberAliasCleanupTail,
    };
    for (RenderedLuaCleanupStepFn step : pre_cleanup_steps) {
        try_keep_cleanup_change(step);
    }

    if (options.include_dead_temporary_alias_cleanup) {
        try_keep_cleanup_change(refii::lua::SimplifyRenderedLuaDeadTemporaryAliasAssignments);
    }

    ApplySharedRenderedLuaCleanupSteps(
        refii::lua::internal::RenderedLuaCleanupProfile::kValidatedOutput,
        normalized_lines,
        [&](const auto& rule) { try_keep_cleanup_change(rule.apply); });

    const std::array<RenderedLuaCleanupStepFn, 8> post_cleanup_steps = {
        refii::lua::HoistRenderedLuaSingleWhileElseTailsForExactParity,
        refii::lua::CollapseRenderedLuaNestedSharedElseGuardsForExactParity,
        refii::lua::CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity,
        refii::lua::RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
        ::RestoreRenderedLuaEmptyEnumTextElseIfAssignmentsForParity,
        refii::lua::NormalizeRenderedLuaTimingRelationalCallOrderForParity,
        refii::lua::SimplifyRenderedLuaRepeatedLocalExpressionUsesForParity,
        refii::lua::SimplifyRenderedLuaRepeatedMethodExpressionConditionsForParity,
    };
    for (RenderedLuaCleanupStepFn step : post_cleanup_steps) {
        try_keep_cleanup_change(step);
    }
    if (options.include_nested_duplicate_return_guard_cleanup) {
        try_keep_cleanup_change(refii::lua::ExpandRenderedLuaTerminalIfActionEmptyElseForParity);
        try_keep_cleanup_change(refii::lua::CollapseRenderedLuaNestedDuplicateReturnGuardsForParity);
    }
    if (options.include_repeated_multi_return_predicate_cleanup) {
        try_keep_cleanup_change(::NormalizeRenderedLuaRepeatedMultiReturnPredicateForParity);
    }

    if (options.include_namespace_alias_cleanup) {
        try_keep_cleanup_change(refii::lua::SimplifyRenderedLuaTopLevelNamespaceTableAliasChains);
    }
    if (options.include_ordered_compare_self_cleanup_tail) {
        try_keep_cleanup_change(refii::lua::ComposeRenderedLuaOrderedCompareSelfCleanupTail);
    }
    try_keep_cleanup_change(refii::lua::ComposeRenderedLuaConditionTempGuardCleanupTailForValidated);

    if (changed_any != nullptr) {
        *changed_any = cleanup_changed;
    }
    if (!cleanup_changed) {
        return original_source;
    }
    return std::string(cleanup_context.EnsureSource());
}
