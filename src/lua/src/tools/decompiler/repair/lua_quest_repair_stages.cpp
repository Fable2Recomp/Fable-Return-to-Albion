#include "tools/decompiler/lua_decompiler_internal.h"

namespace refii::lua::internal {

namespace {

bool RunQuestSimpleRepair(LuaQuestSimpleRepairFunction function,
                          std::string& decompiled_source,
                          std::string* note) {
    return function != nullptr ? function(decompiled_source, note) : false;
}

bool RunQuestValidatedRepair(LuaQuestValidatedRepairFunction function,
                             std::string& decompiled_source,
                             std::string_view validation_error,
                             std::string* note) {
    return function != nullptr ? function(decompiled_source, validation_error, note) : false;
}

}  // namespace

bool RunSharedQuestWaitRepairStage(std::string& decompiled_source,
                                   std::string_view validation_error,
                                   std::string* note,
                                   const SharedQuestWaitRepairStageOptions& options,
                                   const LuaQuestRepairStageOps& ops) {
    const auto deadline_exceeded = [&]() {
        return ShouldAbortCurrentDecompileRepair(note, "quest_wait_stage");
    };
    bool stage_changed = false;
    std::string stage_validation_error(validation_error);
    bool stage_validation_error_ready = !validation_error.empty();
    const auto current_validation_error = [&]() -> std::string_view {
        if (!stage_validation_error_ready) {
            stage_validation_error = ValidateLuaRepairSource(decompiled_source);
            stage_validation_error_ready = true;
        }
        return stage_validation_error;
    };
    const auto invalidate_validation_error = [&]() {
        stage_validation_error.clear();
        stage_validation_error_ready = false;
    };
    stage_changed |=
        RunQuestSimpleRepair(ops.try_split_duplicated_else_wait_ladders, decompiled_source, note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_rewrite_repeat_if_yield_else_continuations, decompiled_source, note);
    stage_changed |=
        RunQuestSimpleRepair(ops.try_rewrite_repeat_state_ladder_loops, decompiled_source, note);
    stage_changed |=
        RunQuestSimpleRepair(ops.try_rewrite_repeat_leading_state_loops, decompiled_source, note);
    stage_changed |=
        RunQuestSimpleRepair(ops.try_rewrite_repeat_state_machine_loops, decompiled_source, note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_rewrite_repeat_monitor_loops_without_until, decompiled_source, note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_break_empty_then_yield_else_continuations, decompiled_source, note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_break_repeated_wait_else_continuations, decompiled_source, note);
    if (deadline_exceeded()) {
        return stage_changed;
    }
    if (options.include_reported_repeat_repair) {
        const bool reported_repeat_changed = RunQuestValidatedRepair(
            ops.try_rewrite_reported_dangling_repeat_loop,
            decompiled_source,
            current_validation_error(),
            note);
        stage_changed |= reported_repeat_changed;
        if (reported_repeat_changed) {
            invalidate_validation_error();
        }
    }
    stage_changed |=
        RunQuestSimpleRepair(ops.try_rewrite_dangling_repeat_loops, decompiled_source, note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_lift_state_chain_after_do_return_end, decompiled_source, note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_lift_state_continuation_after_wait_loop, decompiled_source, note);
    stage_changed |= RunQuestValidatedRepair(ops.try_rewrite_do_return_end_branches,
                                             decompiled_source,
                                             validation_error,
                                             note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_rewrite_immediate_yield_else_wait_loops, decompiled_source, note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_strip_malformed_repeat_wait_wrappers, decompiled_source, note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_lift_malformed_yield_wait_continuations, decompiled_source, note);
    if (deadline_exceeded()) {
        return stage_changed;
    }
    if (options.include_extended_wait_repairs) {
        stage_changed |= RunQuestSimpleRepair(
            ops.try_simplify_rendered_wait_loop_patterns, decompiled_source, note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_rewrite_balanced_outer_true_guarded_wait_blocks, decompiled_source, note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_rewrite_nested_repeated_wait_continuations, decompiled_source, note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_break_suppressed_else_wait_continuations, decompiled_source, note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_rewrite_suppressed_else_sentinel_while_loops, decompiled_source, note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_lift_true_wrapped_wait_loop_continuations, decompiled_source, note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_rewrite_yield_trapped_wait_continuations, decompiled_source, note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_flatten_degenerate_true_wait_wrappers, decompiled_source, note);
        const bool dangling_do_before_boundary_changed = RunQuestValidatedRepair(
            ops.try_strip_dangling_do_before_boundary,
            decompiled_source,
            current_validation_error(),
            note);
        stage_changed |= dangling_do_before_boundary_changed;
        if (dangling_do_before_boundary_changed) {
            invalidate_validation_error();
        }
        const bool dangling_do_inside_true_loop_changed = RunQuestValidatedRepair(
            ops.try_strip_dangling_do_inside_true_loop,
            decompiled_source,
            current_validation_error(),
            note);
        stage_changed |= dangling_do_inside_true_loop_changed;
        if (dangling_do_inside_true_loop_changed) {
            invalidate_validation_error();
        }
        stage_changed |= RunQuestSimpleRepair(
            ops.try_lift_trailing_yield_across_suppressed_close_run, decompiled_source, note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_collapse_suppressed_else_yield_tail_state_loops,
            decompiled_source,
            note);
        stage_changed |= RunQuestSimpleRepair(
            ops.try_split_repeated_wait_collection_loops, decompiled_source, note);
    }
    return stage_changed;
}

bool RunSharedQuestOwnerNormalizationStage(std::string& decompiled_source,
                                           std::string* note,
                                           const LuaQuestRepairStageOps& ops) {
    return RunQuestSimpleRepair(
        ops.try_normalize_quest_owner_continuation_cleanup, decompiled_source, note);
}

bool RunPreCleanOwnerAndReferenceCleanupStage(std::string& decompiled_source,
                                              std::string* note,
                                              const LuaQuestRepairStageOps& ops) {
    const auto deadline_exceeded = [&]() {
        return ShouldAbortCurrentDecompileRepair(note, "quest_preclean_stage");
    };
    bool stage_changed = false;
    stage_changed |= RunSharedQuestOwnerNormalizationStage(decompiled_source, note, ops);
    if (deadline_exceeded()) {
        return stage_changed;
    }
    bool post_owner_reference_changed = false;
    post_owner_reference_changed |= RunQuestSimpleRepair(
        ops.try_rewrite_immediate_anonymous_function_invocations, decompiled_source, note);
    post_owner_reference_changed |= RunQuestSimpleRepair(
        ops.try_rewrite_split_method_reference_self_calls, decompiled_source, note);
    post_owner_reference_changed |= RunQuestSimpleRepair(
        ops.try_rewrite_bare_method_reference_constructor_assignments,
        decompiled_source,
        note);
    post_owner_reference_changed |= RunQuestSimpleRepair(
        ops.try_rewrite_bare_method_reference_assignments, decompiled_source, note);
    post_owner_reference_changed |= RunQuestSimpleRepair(
        ops.try_rewrite_invalid_method_field_assignments, decompiled_source, note);
    post_owner_reference_changed |= RunQuestSimpleRepair(
        ops.try_apply_shared_unhandled_if_comment_cleanup, decompiled_source, note);
    stage_changed |= post_owner_reference_changed;
    if (post_owner_reference_changed && !deadline_exceeded()) {
        stage_changed |= RunSharedQuestOwnerNormalizationStage(decompiled_source, note, ops);
    }
    return stage_changed;
}

bool RunOrderedOwnerAndCloseCleanupStage(std::string& decompiled_source,
                                         std::string_view validation_error,
                                         std::string* note,
                                         const LuaQuestRepairStageOps& ops) {
    const auto deadline_exceeded = [&]() {
        return ShouldAbortCurrentDecompileRepair(note, "quest_owner_stage");
    };
    bool stage_changed = false;
    stage_changed |= RunSharedQuestOwnerNormalizationStage(decompiled_source, note, ops);
    if (deadline_exceeded()) {
        return stage_changed;
    }
    stage_changed |= RunQuestSimpleRepair(
        ops.try_strip_stale_suppressed_close_runs_before_top_level_boundaries,
        decompiled_source,
        note);
    stage_changed |= RunQuestValidatedRepair(
        ops.try_restore_structured_close_runs_before_top_level_boundaries,
        decompiled_source,
        validation_error,
        note);
    stage_changed |= RunQuestSimpleRepair(
        ops.try_strip_redundant_end_before_top_level_boundary, decompiled_source, note);
    return stage_changed;
}

}  // namespace refii::lua::internal
