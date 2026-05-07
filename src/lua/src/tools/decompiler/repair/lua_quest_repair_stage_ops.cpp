#include "tools/decompiler/repair/lua_quest_repair_stage_ops.h"

#include "tools/decompiler/rendered/shared_cleanup_rules.h"
#include "tools/decompiler/syntax/lua_raw_invalid_structural_normalization.h"
#include "tools/decompiler/syntax/lua_syntax_state_loop_repairs.h"

namespace refii::lua::internal {

const LuaQuestRepairStageOps& GetLuaQuestRepairStageOps() {
    static const LuaQuestRepairStageOps kOps = {
        .try_split_duplicated_else_wait_ladders = TrySplitDuplicatedElseWaitLadders,
        .try_rewrite_repeat_if_yield_else_continuations =
            TryRewriteRepeatIfYieldElseContinuations,
        .try_rewrite_repeat_state_ladder_loops = TryRewriteRepeatStateLadderLoops,
        .try_rewrite_repeat_leading_state_loops = TryRewriteRepeatLeadingStateLoops,
        .try_rewrite_repeat_state_machine_loops = TryRewriteRepeatStateMachineLoops,
        .try_rewrite_repeat_monitor_loops_without_until =
            TryRewriteRepeatMonitorLoopsWithoutUntil,
        .try_break_empty_then_yield_else_continuations =
            TryBreakEmptyThenYieldElseContinuations,
        .try_break_repeated_wait_else_continuations = TryBreakRepeatedWaitElseContinuations,
        .try_rewrite_reported_dangling_repeat_loop = TryRewriteReportedDanglingRepeatLoop,
        .try_rewrite_dangling_repeat_loops = TryRewriteDanglingRepeatLoops,
        .try_lift_state_chain_after_do_return_end = TryLiftStateChainAfterDoReturnEnd,
        .try_lift_state_continuation_after_wait_loop = TryLiftStateContinuationAfterWaitLoop,
        .try_rewrite_do_return_end_branches = TryRewriteDoReturnEndBranches,
        .try_rewrite_immediate_yield_else_wait_loops = TryRewriteImmediateYieldElseWaitLoops,
        .try_strip_malformed_repeat_wait_wrappers = TryStripMalformedRepeatWaitWrappers,
        .try_lift_malformed_yield_wait_continuations =
            TryLiftMalformedYieldWaitContinuations,
        .try_simplify_rendered_wait_loop_patterns = TrySimplifyRenderedWaitLoopPatterns,
        .try_rewrite_balanced_outer_true_guarded_wait_blocks =
            TryRewriteBalancedOuterTrueGuardedWaitBlocks,
        .try_rewrite_nested_repeated_wait_continuations =
            TryRewriteNestedRepeatedWaitContinuations,
        .try_break_suppressed_else_wait_continuations =
            TryBreakSuppressedElseWaitContinuations,
        .try_rewrite_suppressed_else_sentinel_while_loops =
            TryRewriteSuppressedElseSentinelWhileLoops,
        .try_lift_true_wrapped_wait_loop_continuations =
            TryLiftTrueWrappedWaitLoopContinuations,
        .try_rewrite_yield_trapped_wait_continuations =
            TryRewriteYieldTrappedWaitContinuations,
        .try_flatten_degenerate_true_wait_wrappers = TryFlattenDegenerateTrueWaitWrappers,
        .try_strip_dangling_do_before_boundary = TryStripDanglingDoBeforeBoundary,
        .try_strip_dangling_do_inside_true_loop = TryStripDanglingDoInsideTrueLoop,
        .try_lift_trailing_yield_across_suppressed_close_run =
            TryLiftTrailingYieldAcrossSuppressedCloseRun,
        .try_collapse_suppressed_else_yield_tail_state_loops =
            TryCollapseSuppressedElseYieldTailStateLoops,
        .try_split_repeated_wait_collection_loops = TrySplitRepeatedWaitCollectionLoops,
        .try_normalize_quest_owner_continuation_cleanup =
            TryNormalizeQuestOwnerContinuationCleanup,
        .try_rewrite_immediate_anonymous_function_invocations =
            TryRewriteImmediateAnonymousFunctionInvocations,
        .try_rewrite_split_method_reference_self_calls =
            TryRewriteSplitMethodReferenceSelfCalls,
        .try_rewrite_bare_method_reference_constructor_assignments =
            TryRewriteBareMethodReferenceConstructorAssignments,
        .try_rewrite_bare_method_reference_assignments =
            TryRewriteBareMethodReferenceAssignments,
        .try_rewrite_invalid_method_field_assignments =
            TryRewriteInvalidMethodFieldAssignments,
        .try_apply_shared_unhandled_if_comment_cleanup =
            TryApplySharedUnhandledIfCommentCleanup,
        .try_strip_stale_suppressed_close_runs_before_top_level_boundaries =
            TryStripStaleSuppressedCloseRunsBeforeTopLevelBoundaries,
        .try_restore_structured_close_runs_before_top_level_boundaries =
            TryRestoreStructuredCloseRunsBeforeTopLevelBoundaries,
        .try_strip_redundant_end_before_top_level_boundary =
            TryStripRedundantEndBeforeTopLevelBoundary,
    };
    return kOps;
}

} // namespace refii::lua::internal
