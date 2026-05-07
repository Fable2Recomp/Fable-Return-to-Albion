#pragma once

#include "tools/decompiler/rendered/shared_cleanup_rules.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <array>
#include <functional>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace refii::lua::internal {

using RenderedLuaCleanupStepFn = bool (*)(std::vector<std::string>* lines);
using RenderedCleanupRuleVisitor =
    std::function<void(const RenderedCleanupRuleDescriptor& rule)>;
using SharedCleanupStepDispatcher =
    std::function<void(RenderedLuaCleanupProfile profile,
                       std::vector<std::string>* lines,
                       const RenderedCleanupRuleVisitor& apply_rule)>;

struct ValidatedRenderedCleanupPipelineOptions {
    bool include_dead_temporary_alias_cleanup = false;
    bool include_namespace_alias_cleanup = false;
    bool include_ordered_compare_self_cleanup_tail = false;
    bool include_nested_duplicate_return_guard_cleanup = false;
    bool include_repeated_multi_return_predicate_cleanup = false;

    RenderedLuaCleanupStepFn localize_top_level_temporary_assignments = nullptr;
    RenderedLuaCleanupStepFn restore_empty_enum_text_else_if_assignments = nullptr;
    RenderedLuaCleanupStepFn normalize_repeated_multi_return_predicate = nullptr;
    SharedCleanupStepDispatcher apply_shared_cleanup_steps;
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
    bool allow_call_reassignment_self_compare_cleanup);
void AppendRenderedLuaNonexactLightCleanupNotes(
    const RenderedLuaNonexactLightCleanupResult& cleanup_result,
    std::string_view note_prefix,
    std::string* note);

template <typename KeepCandidateFn>
std::string ApplyValidatedRenderedCleanupPipeline(
    std::string source_text,
    const ValidatedRenderedCleanupPipelineOptions& options,
    KeepCandidateFn&& keep_candidate,
    bool* changed_any) {
    LuaMutableTextContext cleanup_context(std::move(source_text));
    const std::string original_source = std::string(cleanup_context.EnsureSource());
    std::vector<std::string>* normalized_lines = cleanup_context.EnsureMutableLines();
    const bool had_trailing_newline = cleanup_context.HadTrailingNewline();
    std::string current_source = original_source;
    bool cleanup_changed = false;
    std::unordered_set<std::string> seen_sources;
    seen_sources.insert(current_source);

    const auto try_keep_cleanup_change = [&](auto step) {
        if (step == nullptr || normalized_lines == nullptr) {
            return;
        }

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
        options.localize_top_level_temporary_assignments,
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

    if (options.apply_shared_cleanup_steps) {
        options.apply_shared_cleanup_steps(
            RenderedLuaCleanupProfile::kValidatedOutput,
            normalized_lines,
            [&](const auto& rule) { try_keep_cleanup_change(rule.apply); });
    }

    const std::array<RenderedLuaCleanupStepFn, 8> post_cleanup_steps = {
        refii::lua::HoistRenderedLuaSingleWhileElseTailsForExactParity,
        refii::lua::CollapseRenderedLuaNestedSharedElseGuardsForExactParity,
        refii::lua::CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity,
        refii::lua::RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
        options.restore_empty_enum_text_else_if_assignments,
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
        try_keep_cleanup_change(options.normalize_repeated_multi_return_predicate);
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

}  // namespace refii::lua::internal
