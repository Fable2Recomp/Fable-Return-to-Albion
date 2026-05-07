#include "tools/decompiler/syntax/lua_ordered_syntax_repair_passes.h"

#include "tools/decompiler/rendered/repair_state_keys.h"
#include "tools/decompiler/repair/lua_decompile_repair_context.h"
#include "tools/decompiler/repair/lua_quest_repair_stage_ops.h"
#include "tools/decompiler/syntax/lua_syntax_generator_repairs.h"

#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>

namespace refii::lua::internal {
namespace {

void AppendNoProgressNote(std::string* note, std::string_view key) {
    if (note == nullptr || key.empty()) {
        return;
    }
    *note = JoinNotes(*note, std::string(key));
}

} // namespace

SyntaxRepairAttemptResult TryRunOrderedSyntaxRepairPass(
    std::string& decompiled_source,
    std::string& validation_error,
    std::string_view validation_label,
    std::string* note,
    bool* tried_insert_before_reported_line,
    const OrderedSyntaxRepairPassOptions& options) {
    bool changed = false;
    const auto run_late_anchored_repairs =
        [&](bool* changed_out = nullptr) -> SyntaxRepairAttemptResult {
        const SyntaxRepairAttemptResult late_anchored_result =
            TryRunAnchoredValidatedSyntaxRepairs(
                decompiled_source, validation_error, validation_label, note);
        if (late_anchored_result == SyntaxRepairAttemptResult::kValidated) {
            return SyntaxRepairAttemptResult::kValidated;
        }
        if (late_anchored_result == SyntaxRepairAttemptResult::kChanged &&
            changed_out != nullptr) {
            *changed_out = true;
        }
        return late_anchored_result;
    };
    struct OrderedQuestStageContext {
        std::string* source = nullptr;
        std::string_view validation_error;
        std::string* note = nullptr;
    };

    const auto run_ordered_early_structural_preclean = [](void* raw_context) {
        if (raw_context == nullptr) {
            return false;
        }
        OrderedQuestStageContext& context =
            *static_cast<OrderedQuestStageContext*>(raw_context);
        bool stage_changed = false;
        stage_changed |= TryMaterializeMissingEndWarnings(*context.source, context.note);
        stage_changed |= TryMaterializeCommentedControlLines(*context.source, context.note);
        stage_changed |=
            TryCommentDanglingControlLinesAroundInvalidBlocks(*context.source, context.note);
        return stage_changed;
    };

    const auto run_ordered_quest_wait_repairs = [](void* raw_context) {
        if (raw_context == nullptr) {
            return false;
        }
        OrderedQuestStageContext& context =
            *static_cast<OrderedQuestStageContext*>(raw_context);
        return RunSharedQuestWaitRepairStage(
            *context.source,
            context.validation_error,
            context.note,
            SharedQuestWaitRepairStageOptions{
                .include_reported_repeat_repair = true,
                .include_extended_wait_repairs = true,
            },
            GetLuaQuestRepairStageOps());
    };

    const auto run_ordered_owner_cleanup = [](void* raw_context) {
        if (raw_context == nullptr) {
            return false;
        }
        OrderedQuestStageContext& context =
            *static_cast<OrderedQuestStageContext*>(raw_context);
        return RunOrderedOwnerAndCloseCleanupStage(
            *context.source, context.validation_error, context.note, GetLuaQuestRepairStageOps());
    };

    OrderedQuestStageContext staged_context = {
        .source = &decompiled_source,
        .validation_error = validation_error,
        .note = note,
    };
    changed |= RunLuaRepairStageSequence({
        .context = &staged_context,
        .run_early_structural_preclean = run_ordered_early_structural_preclean,
        .run_quest_wait_repairs = run_ordered_quest_wait_repairs,
        .run_owner_cleanup = run_ordered_owner_cleanup,
    });

    const SyntaxRepairAttemptResult anchored_result = TryRunAnchoredValidatedSyntaxRepairs(
        decompiled_source, validation_error, validation_label, note);
    if (anchored_result != SyntaxRepairAttemptResult::kNoChange) {
        return anchored_result;
    }

    changed |= TryRewriteDanglingYieldElseWaitLoops(decompiled_source, note);
    changed |= TryCollapseDuplicatedElseLines(
        decompiled_source, validation_error, note);
    changed |= TryStripDanglingEndBeforeElse(decompiled_source, note);
    changed |= TryRemoveExcessEndLines(decompiled_source, validation_error, note);
    changed |=
        TryRemoveEndsBeforeTopLevelBoundaries(decompiled_source, validation_error, note);
    changed |= TryStripStandaloneLocalArtifactLines(decompiled_source, note);
    changed |= TryMergeSplitLocalMultiResultAssignments(decompiled_source, note);
    changed |= TryRewriteAnonymousFunctionCallThroughNamedLocal(
        decompiled_source, validation_error, note);
    changed |= TryStripEmptyElseBlocks(decompiled_source, note);
    changed |= TryRestoreStrandedTopLevelHelperFunctionBlocks(
        decompiled_source, validation_error, note);
    changed |= TryNameOrphanTopLevelAnonymousFunctionCalls(
        decompiled_source, validation_error, note);
    changed |= TryRewriteImmediateAnonymousFunctionInvocations(decompiled_source, note);
    changed |= TryRewriteSplitMethodReferenceSelfCalls(decompiled_source, note);
    if (options.include_top_level_leakage_cleanup) {
        changed |= TryStripTopLevelLeakageAfterFunction(
            decompiled_source, validation_error, note);
    }
    changed |= TryStripDanglingDoBeforeElse(decompiled_source, note);
    if (options.include_member_reference_repairs) {
        changed |= TryRewriteBareMethodReferenceConstructorAssignments(
            decompiled_source, note);
        changed |= TryRewriteBareMethodReferenceAssignments(decompiled_source, note);
        changed |= TryRewriteInvalidMethodFieldAssignments(decompiled_source, note);
    }
    if (options.include_for_generator_repairs) {
        changed |= TryRewriteForGeneratorElseArtifacts(decompiled_source, note);
    }
    if (options.include_generator_comment_repairs) {
        changed |= TryCommentForGeneratorArtifacts(decompiled_source, note);
    }
    if (options.include_while_true_to_repeat) {
        changed |= TryRewriteWhileTrueToRepeat(
            decompiled_source, validation_error, note);
    }
    changed |= TryStripDanglingDoAfterUntil(decompiled_source, note);
    if (options.include_insert_end_before_return) {
        changed |= TryInsertEndBeforeReturn(decompiled_source, validation_error, note);
    }
    if (options.include_insert_end_before_until) {
        changed |= TryInsertEndBeforeUntil(decompiled_source, validation_error, note);
    }
    if (options.include_insert_until_before_trailing_end) {
        changed |= TryInsertUntilBeforeTrailingEnd(
            decompiled_source, validation_error, note);
    }

    if (changed) {
        validation_error = ValidateLuaRepairSource(decompiled_source);
        const SyntaxRepairAttemptResult late_anchored_result =
            run_late_anchored_repairs(&changed);
        if (late_anchored_result == SyntaxRepairAttemptResult::kValidated) {
            return SyntaxRepairAttemptResult::kValidated;
        }
        if (!validation_error.empty() &&
            TryRemoveEndsBeforeTopLevelBoundaries(
                decompiled_source, validation_error, note)) {
            changed = true;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            const SyntaxRepairAttemptResult after_boundary_cleanup_anchored_result =
                run_late_anchored_repairs(&changed);
            if (after_boundary_cleanup_anchored_result ==
                SyntaxRepairAttemptResult::kValidated) {
                return SyntaxRepairAttemptResult::kValidated;
            }
        }
        if (!validation_error.empty() &&
            TryInsertEndBeforeElse(decompiled_source, validation_error, note)) {
            changed = true;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            const SyntaxRepairAttemptResult after_insert_end_anchored_result =
                run_late_anchored_repairs(&changed);
            if (after_insert_end_anchored_result ==
                SyntaxRepairAttemptResult::kValidated) {
                return SyntaxRepairAttemptResult::kValidated;
            }
        }
    }

    if (!changed && tried_insert_before_reported_line != nullptr &&
        !*tried_insert_before_reported_line &&
        TryInsertEndBeforeReportedLine(decompiled_source, validation_error, note)) {
        *tried_insert_before_reported_line = true;
        changed = true;
    }
    if (!changed && options.include_insert_ends_before_top_level_boundaries &&
        TryInsertEndsBeforeTopLevelBoundaries(
            decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed && TryInsertEndBeforeElse(decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed &&
        TryRepairNoLoopToBreakNearElse(decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed &&
        TryRemovePrematureYieldLoopEndBeforeBreakElse(
            decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed && TryAppendMissingUntil(decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed &&
        TryInsertEndBeforeMalformedTopLevelBoundary(
            decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed &&
        TryMoveTrailingEndsBeforeReportedBoundary(
            decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed &&
        TryCloseReportedTopLevelFunctionBeforeSiblingBoundary(
            decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed && TryBalanceOpenBlocks(decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed && TryAppendMissingEnds(decompiled_source, validation_error, note)) {
        changed = true;
    }
    if (!changed) {
        return SyntaxRepairAttemptResult::kNoChange;
    }

    changed |=
        RunSharedQuestOwnerNormalizationStage(decompiled_source, note, GetLuaQuestRepairStageOps());

    validation_error = ValidateLuaRepairSource(decompiled_source);
    return validation_error.empty() ? SyntaxRepairAttemptResult::kValidated
                                    : SyntaxRepairAttemptResult::kChanged;
}

SyntaxRepairAttemptResult RunOrderedSyntaxRepairPassAttempts(
    std::string& decompiled_source,
    std::string& validation_error,
    std::string_view validation_label,
    std::string* note,
    bool* tried_insert_before_reported_line,
    const OrderedSyntaxRepairPassOptions& options,
    int max_attempts,
    bool note_cycles) {
    bool changed_any = false;
    std::unordered_set<std::string> seen_states;
    std::unordered_set<std::string> seen_quality_states;
    std::unordered_map<std::string, int> seen_family_states;
    for (int attempt = 0; attempt < max_attempts && !validation_error.empty(); ++attempt) {
        if (ShouldAbortCurrentDecompileRepair(note, validation_label)) {
            break;
        }
        const std::string before_state =
            BuildRepairProgressStateKey(decompiled_source, validation_error);
        const std::string before_quality_state =
            BuildRepairQualityStateKey(decompiled_source, validation_error);
        const std::string before_family_state =
            BuildRepairFamilyStateKey(decompiled_source, validation_error);
        if (!seen_states.insert(before_state).second) {
            if (note_cycles && note != nullptr) {
                *note = JoinNotes(*note, "syntax_repair_cycle_detected");
            }
            break;
        }
        if (!seen_quality_states.insert(before_quality_state).second) {
            if (note_cycles) {
                AppendNoProgressNote(note, "syntax_repair_no_progress");
            }
            break;
        }
        if (++seen_family_states[before_family_state] > GetCurrentRepairFamilyRepeatBudget()) {
            if (note_cycles) {
                AppendNoProgressNote(note, "syntax_repair_no_progress");
            }
            break;
        }

        const SyntaxRepairAttemptResult repair_result = TryRunOrderedSyntaxRepairPass(
            decompiled_source,
            validation_error,
            validation_label,
            note,
            tried_insert_before_reported_line,
            options);
        if (repair_result == SyntaxRepairAttemptResult::kNoChange) {
            break;
        }
        if (repair_result == SyntaxRepairAttemptResult::kValidated) {
            return SyntaxRepairAttemptResult::kValidated;
        }
        if (TryNormalizeQuestOwnerContinuationCleanup(decompiled_source, note)) {
            validation_error = ValidateLuaSource(decompiled_source, std::string(validation_label));
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
        }
        changed_any = true;

        const std::string after_quality_state =
            BuildRepairQualityStateKey(decompiled_source, validation_error);
        if (after_quality_state == before_quality_state) {
            if (note_cycles) {
                AppendNoProgressNote(note, "syntax_repair_no_progress");
            }
            break;
        }
    }

    return changed_any ? SyntaxRepairAttemptResult::kChanged
                       : SyntaxRepairAttemptResult::kNoChange;
}

} // namespace refii::lua::internal
