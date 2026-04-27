#include "tools/native_lua_decompiler.h"
#include "tools/lua_bytecode_oracles.h"
#include "tools/lua_decompiler_internal.h"
#include "tools/native_lua_chunk.h"
#include "tools/straight_line_native_render.h"
#include "tools/decompiler/lua51_analysis.h"
#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/straight_line_render.h"
#include "tools/decompiler/rendered_lua_text.h"
#include "tools/decompiler/native_first.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdlib>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iterator>
#include <limits>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <tuple>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#ifdef __cplusplus
extern "C" {
#endif
#ifdef __cplusplus
}
#endif

#if defined(_WIN32)
#include <process.h>
#else
#include <fcntl.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>
#endif

namespace {

constexpr std::string_view kLuaSignature = "\x1bLua";
constexpr int kRunTimedOut = -2;
constexpr int kRunSpawnFailed = -3;
constexpr int kRunSignaledBase = -1000;
constexpr std::size_t kManifestFlushStride = 32;
constexpr std::uintmax_t kLargeNativeMainChunkSubprocessThresholdBytes = 192 * 1024;
constexpr std::uintmax_t kLargeNativeMainChunkInProcessFallbackMaxBytes = 320 * 1024;
constexpr int kReferenceFunctionRepairMaxPasses = 16;
constexpr std::uintmax_t kVeryLargeChunkFastFailThresholdBytes = 170000;
constexpr std::size_t kMaxTransientCleanupCacheEntries = 24;
constexpr std::size_t kMaxTransientRenderedFunctionCacheEntries = 96;
constexpr std::size_t kMaxCachedCleanupSourceBytes = 256 * 1024;

struct RenderedFunctionCacheEntry {
    bool attempted = false;
    bool success = false;
    std::string rendered_function;
};

struct MainChunkRenderCacheEntry {
    bool attempted = false;
    bool success = false;
    std::string rendered_source;
    std::vector<std::string> note_suffixes;
};

struct CleanupCacheEntry {
    bool attempted = false;
    std::string cleaned_source;
};

struct LuaDecompilerSessionCache {
    std::unordered_map<std::string, RenderedFunctionCacheEntry> rendered_functions;
    std::unordered_map<std::string, MainChunkRenderCacheEntry> main_chunk_renders;
    std::unordered_map<std::string, CleanupCacheEntry> cleanup_results;
};

struct DecompileRepairContext {
    bool prefer_exhaustive_rescue = true;
    bool outlier_candidate = false;
    bool previous_failed_file = false;
    bool unfinished_active = false;
    bool deadline_active = false;
    bool deadline_noted = false;
    std::chrono::steady_clock::time_point deadline{};
};

thread_local LuaDecompilerSessionCache g_lua_decompiler_session_cache;
thread_local DecompileRepairContext g_decompile_repair_context;

template <typename Map>
void ClearAndReleaseMap(Map* map) {
    if (map == nullptr || map->empty()) {
        return;
    }
    Map empty;
    map->swap(empty);
}

bool ShouldPreserveFailedOutputs() {
    const char* value = std::getenv("REFII_LUA_PRESERVE_FAILED_OUTPUTS");
    if (value == nullptr) {
        return false;
    }
    const std::string_view text(value);
    return !text.empty() && text != "0" && text != "false" && text != "FALSE";
}

std::filesystem::path BuildPreservedFailedOutputPath(
    const std::filesystem::path& output_path) {
    return output_path.parent_path() / "_failed" / output_path.filename();
}

using refii::lua::internal::ResolvedInput;
using refii::lua::internal::AppendSyntaxErrorNotes;
using refii::lua::internal::AppendReindentedLuaBlock;
using refii::lua::internal::CompileLuaSourceToChunkCached;
using refii::lua::internal::ComputeStableBytecodeIdentity;
using refii::lua::internal::ExtractLuaErrorLine;
using refii::lua::internal::ExtractLeadingIdentifier;
using refii::lua::internal::ExtractLocalDeclarationName;
using refii::lua::internal::FindBalancedLuaBlockEnd;
using refii::lua::internal::FindFirstNonEmptyLuaLineInRange;
using refii::lua::internal::FindLastNonEmptyLuaLineInRange;
using refii::lua::internal::FindNextNonEmptyLuaLine;
using refii::lua::internal::FindNextSignificantLuaLine;
using refii::lua::internal::FindPreviousNonEmptyLuaLine;
using refii::lua::internal::GetParsedChunkCacheEntry;
using refii::lua::internal::GetLuaLeadingIndent;
using refii::lua::internal::GetLuaLineIndent;
using refii::lua::internal::ContainsIdentifierReference;
using refii::lua::internal::ContainsWord;
using refii::lua::internal::IsLuaYieldWaitLine;
using refii::lua::internal::IsBareLuaCallSuffix;
using refii::lua::internal::IsDecompilerMarkerSummaryBetter;
using refii::lua::internal::IsValidationOutcomeBetter;
using refii::lua::internal::IsLuaOutputQualityBetter;
using refii::lua::internal::IsLuaOutputQualityNoWorse;
using refii::lua::internal::IsSuppressedControlMarker;
using refii::lua::internal::IsSuppressedElseControlMarker;
using refii::lua::internal::LuaOutputQualitySummary;
using refii::lua::internal::DecompiledMarkerSummary;
using refii::lua::internal::LooksLikeTopLevelBoundary;
using refii::lua::internal::LooksLikeDecompilerTemporaryName;
using refii::lua::internal::LuaQuestRepairStageOps;
using refii::lua::internal::OrderedSyntaxRepairPassOptions;
using refii::lua::internal::ParseSimpleRenderedAssignment;
using refii::lua::internal::ParseLuaIfCondition;
using refii::lua::internal::ParseLuaWhileCondition;
using refii::lua::internal::ParsedChunkCacheEntry;
using refii::lua::internal::ComputeLuaBlockDelta;
using refii::lua::internal::RunLuaRepairStageSequence;
using refii::lua::internal::RunOrderedOwnerAndCloseCleanupStage;
using refii::lua::internal::RunPreCleanOwnerAndReferenceCleanupStage;
using refii::lua::internal::RunSharedQuestOwnerNormalizationStage;
using refii::lua::internal::RunSharedQuestWaitRepairStage;
using refii::lua::internal::SharedQuestWaitRepairStageOptions;
using refii::lua::internal::SummarizeDecompilerMarkers;
using refii::lua::internal::SummarizeLuaOutputQuality;
using refii::lua::internal::SummarizeSyntaxError;
using refii::lua::internal::SyntaxRepairAttemptResult;
using refii::lua::internal::DescribeLuaOutputQuality;
using refii::lua::internal::TryInsertEndsBeforeTopLevelBoundaries;
using refii::lua::internal::TryInsertEndBeforeElse;
using refii::lua::internal::TryRepairNoLoopToBreakNearElse;
using refii::lua::internal::TryRemovePrematureYieldLoopEndBeforeBreakElse;
using refii::lua::internal::TryInsertEndBeforeReportedLine;
using refii::lua::internal::TryInsertEndBeforeReturn;
using refii::lua::internal::TryInsertEndBeforeUntil;
using refii::lua::internal::TryInsertUntilBeforeTrailingEnd;
using refii::lua::internal::TryAppendMissingEnds;
using refii::lua::internal::TryAppendMissingUntil;
using refii::lua::internal::TryBalanceOpenBlocks;
using refii::lua::internal::TryCollapseDuplicatedElseLines;
using refii::lua::internal::TryCommentDanglingControlLinesAroundInvalidBlocks;
using refii::lua::internal::TryMaterializeCommentedControlLines;
using refii::lua::internal::TryMaterializeMissingEndWarnings;
using refii::lua::internal::TryMergeSplitLocalMultiResultAssignments;
using refii::lua::internal::TryNameOrphanTopLevelAnonymousFunctionCalls;
using refii::lua::internal::TryCloseReportedTopLevelFunctionBeforeSiblingBoundary;
using refii::lua::internal::TryRemoveExcessEndLines;
using refii::lua::internal::TryRemoveEndsBeforeTopLevelBoundaries;
using refii::lua::internal::TryRepairValidatedTopLevelFunctionBoundaryBleed;
using refii::lua::internal::TryRewriteDanglingYieldElseWaitLoops;
using refii::lua::internal::TryRewriteDanglingRepeatLoops;
using refii::lua::internal::TryRewriteBareMethodReferenceAssignments;
using refii::lua::internal::TryRewriteBareMethodReferenceConstructorAssignments;
using refii::lua::internal::TryBreakEmptyThenYieldElseContinuations;
using refii::lua::internal::TryBreakRepeatedWaitElseContinuations;
using refii::lua::internal::TryBreakSuppressedElseWaitContinuations;
using refii::lua::internal::TryCollapseSuppressedElseYieldTailStateLoops;
using refii::lua::internal::TryFlattenDegenerateTrueWaitWrappers;
using refii::lua::internal::TryLiftMalformedYieldWaitContinuations;
using refii::lua::internal::TryLiftTrailingYieldAcrossSuppressedCloseRun;
using refii::lua::internal::TryLiftTrueWrappedWaitLoopContinuations;
using refii::lua::internal::TryRewriteImmediateAnonymousFunctionInvocations;
using refii::lua::internal::TryRewriteAnonymousFunctionCallThroughNamedLocal;
using refii::lua::internal::TryRewriteImmediateYieldElseWaitLoops;
using refii::lua::internal::TryRewriteInvalidMethodFieldAssignments;
using refii::lua::internal::TryInsertEndBeforeMalformedTopLevelBoundary;
using refii::lua::internal::TryMoveTrailingEndsBeforeReportedBoundary;
using refii::lua::internal::TryRewriteReportedDanglingRepeatLoop;
using refii::lua::internal::TryRewriteBalancedOuterTrueGuardedWaitBlocks;
using refii::lua::internal::TryRewriteNestedRepeatedWaitContinuations;
using refii::lua::internal::TryRewriteDoReturnEndBranches;
using refii::lua::internal::TryRewriteRepeatIfYieldElseContinuations;
using refii::lua::internal::TryRewriteSuppressedElseSentinelWhileLoops;
using refii::lua::internal::TryRestoreStructuredCloseRunsBeforeTopLevelBoundaries;
using refii::lua::internal::TryRewriteYieldTrappedWaitContinuations;
using refii::lua::internal::TryRestoreStrandedTopLevelHelperFunctionBlocks;
using refii::lua::internal::TryRewriteSplitMethodReferenceSelfCalls;
using refii::lua::internal::TrySplitDuplicatedElseWaitLadders;
using refii::lua::internal::TrySplitRepeatedWaitCollectionLoops;
using refii::lua::internal::TryStripMalformedRepeatWaitWrappers;
using refii::lua::internal::TryStripDanglingDoAfterUntil;
using refii::lua::internal::TryStripDanglingDoBeforeBoundary;
using refii::lua::internal::TryStripDanglingDoBeforeElse;
using refii::lua::internal::TryStripDanglingDoInsideTrueLoop;
using refii::lua::internal::TryStripDanglingEndBeforeElse;
using refii::lua::internal::TryStripEmptyElseBlocks;
using refii::lua::internal::TryStripStandaloneLocalArtifactLines;
using refii::lua::internal::TryStripStandaloneDoReturnEndArtifacts;
using refii::lua::internal::TryStripStaleSuppressedCloseRunsBeforeTopLevelBoundaries;
using refii::lua::internal::TryStripRedundantEndBeforeTopLevelBoundary;
using refii::lua::internal::TryStripTopLevelLeakageAfterFunction;
using refii::lua::internal::TryRunAnchoredValidatedSyntaxRepairs;
using refii::lua::internal::TrySimplifyRenderedWaitLoopPatterns;
using refii::lua::internal::TryNormalizeQuestOwnerContinuationCleanup;
using refii::lua::internal::TryRewriteWhileTrueToRepeat;
using refii::lua::internal::ValidateLuaRepairSource;
using refii::lua::internal::ValidateLuaSource;

const DecompileRepairContext& GetCurrentDecompileRepairContext() {
    return g_decompile_repair_context;
}

bool ShouldUseExhaustiveRescueForCurrentFile() {
    return GetCurrentDecompileRepairContext().prefer_exhaustive_rescue;
}

int GetCurrentReferenceRepairPassBudget() {
    if (!ShouldUseExhaustiveRescueForCurrentFile()) {
        return 1;
    }
    return GetCurrentDecompileRepairContext().outlier_candidate ? 4
                                                                : kReferenceFunctionRepairMaxPasses;
}

std::string BuildRepairProgressStateKey(std::string_view source,
                                        std::string_view validation_error);
std::string BuildRepairQualityStateKey(std::string_view source,
                                       std::string_view validation_error);
std::string BuildRepairFamilyStateKey(std::string_view source,
                                      std::string_view validation_error);

int GetCurrentRepairAttemptCap(int default_cap, int nonfailed_outlier_cap) {
    const DecompileRepairContext& context = GetCurrentDecompileRepairContext();
    if (!context.outlier_candidate || context.previous_failed_file ||
        context.unfinished_active ||
        ShouldUseExhaustiveRescueForCurrentFile()) {
        return default_cap;
    }
    return std::min(default_cap, nonfailed_outlier_cap);
}

int GetCurrentRepairFamilyRepeatBudget() {
    const DecompileRepairContext& context = GetCurrentDecompileRepairContext();
    if (!context.outlier_candidate) {
        return 4;
    }
    if (!context.previous_failed_file && !context.unfinished_active &&
        !ShouldUseExhaustiveRescueForCurrentFile()) {
        return 2;
    }
    return 6;
}

bool IsCurrentDecompileRepairDeadlineExceeded() {
    const DecompileRepairContext& context = GetCurrentDecompileRepairContext();
    return context.deadline_active &&
           std::chrono::steady_clock::now() >= context.deadline;
}

bool ShouldAbortCurrentDecompileRepair(std::string* note, std::string_view note_prefix) {
    if (!IsCurrentDecompileRepairDeadlineExceeded()) {
        return false;
    }

    if (note != nullptr && !g_decompile_repair_context.deadline_noted) {
        const std::string prefix =
            note_prefix.empty() ? "repair" : std::string(note_prefix);
        *note = refii::lua::internal::JoinNotes(*note, prefix + "_deadline_exceeded");
        g_decompile_repair_context.deadline_noted = true;
    }
    return true;
}

void AppendNoProgressNote(std::string* note, std::string_view key) {
    if (note == nullptr || key.empty()) {
        return;
    }
    *note = refii::lua::internal::JoinNotes(*note, std::string(key));
}

std::string BuildRepairFamilyStateAttemptKey(std::string_view family,
                                             std::string_view source,
                                             std::string_view validation_error) {
    std::string key;
    key.reserve(family.size() + 1 + 96);
    key.append(family.data(), family.size());
    key.push_back('|');
    key.append(BuildRepairQualityStateKey(source, validation_error));
    return key;
}

bool ShouldAttemptRepairFamilyForState(std::unordered_set<std::string>* seen_family_states,
                                       std::string_view family,
                                       std::string_view source,
                                       std::string_view validation_error,
                                       std::string* note) {
    if (seen_family_states == nullptr || family.empty()) {
        return true;
    }
    const std::string state_key =
        BuildRepairFamilyStateAttemptKey(family, source, validation_error);
    if (seen_family_states->insert(state_key).second) {
        return true;
    }
    AppendNoProgressNote(note, std::string(family) + "_state_repeat_skip");
    return false;
}

constexpr std::size_t kRuntimeLightModeThresholdBytes = 96 * 1024;
constexpr std::size_t kRuntimeHeavyFamilyClampThresholdBytes = 128 * 1024;

bool IsRuntimeLightModePath(const refii::lua::LuaScriptPolicy& policy, std::size_t byte_size) {
    return policy.runtime_hotspot && byte_size >= kRuntimeLightModeThresholdBytes;
}

bool ShouldClampRuntimeHeavyRepairFamilies(const refii::lua::LuaScriptPolicy& policy,
                                           std::size_t byte_size) {
    return byte_size >= kRuntimeHeavyFamilyClampThresholdBytes &&
           (IsRuntimeLightModePath(policy, byte_size) ||
            policy.runtime_behavior_path ||
            policy.runtime_bounded_attempt_ai_combat_hotspot);
}

bool IsRuntimeHeavyRepairFamily(std::string_view family) {
    return family == "native_replace_decompiler_error_functions" ||
           family == "bytecode_function_refinement" ||
           family == "bytecode_reported_function_refinement" ||
           family == "native_candidate_source_repair" ||
           family == "post_restore_native_candidate_repair" ||
           family == "shared_rendered_cleanup" ||
           family == "replace_validated_native_main_chunk" ||
           family == "replace_decompiler_error_functions" ||
           family == "repair_reported_functions_iterative";
}

int GetRuntimeAwareSyntaxRepairAttemptCap(bool is_quest_hotspot,
                                          bool large_chunk,
                                          bool runtime_heavy_family_clamp,
                                          bool force_runtime_light_mode_path) {
    if (is_quest_hotspot) {
        return GetCurrentRepairAttemptCap(4, 2);
    }
    if (force_runtime_light_mode_path) {
        return GetCurrentRepairAttemptCap(large_chunk ? 8 : 20, large_chunk ? 3 : 6);
    }
    if (runtime_heavy_family_clamp) {
        return GetCurrentRepairAttemptCap(large_chunk ? 12 : 36, large_chunk ? 4 : 10);
    }
    return GetCurrentRepairAttemptCap(large_chunk ? 32 : 128,
                                      large_chunk ? 8 : 24);
}

template <typename PassFn>
bool RunBoundedNormalizationPassLoop(int max_passes, PassFn&& run_single_pass) {
    bool changed = false;
    const int pass_cap = std::max(max_passes, 1);
    for (int pass = 0; pass < pass_cap; ++pass) {
        if (IsCurrentDecompileRepairDeadlineExceeded()) {
            break;
        }
        if (!run_single_pass(pass)) {
            break;
        }
        changed = true;
    }
    return changed;
}

template <typename AbortFn, typename RepairFn, typename ValidateFn>
void RunBoundedReferenceRepairLoop(int pass_budget,
                                   bool guard_repeated_source,
                                   std::string* source,
                                   std::string* validation_error,
                                   std::string* note,
                                   std::string_view no_progress_note,
                                   AbortFn&& should_abort,
                                   RepairFn&& try_repair,
                                   ValidateFn&& validate_source) {
    if (source == nullptr || validation_error == nullptr) {
        if (source == nullptr) {
            return;
        }
    }

    std::string local_validation_error;
    std::string* validation_error_ptr = validation_error;
    if (validation_error_ptr == nullptr) {
        local_validation_error = validate_source();
        validation_error_ptr = &local_validation_error;
    }

    std::string previous_source;
    std::string previous_state;
    RunBoundedNormalizationPassLoop(pass_budget, [&](int /*pass*/) {
        if (should_abort()) {
            return false;
        }
        const std::string before_state =
            BuildRepairQualityStateKey(*source, *validation_error_ptr);
        if (guard_repeated_source && *source == previous_source) {
            return false;
        }
        if (guard_repeated_source) {
            previous_source = *source;
        }
        if (!try_repair()) {
            return false;
        }
        *validation_error_ptr = validate_source();
        const std::string after_state =
            BuildRepairQualityStateKey(*source, *validation_error_ptr);
        if (after_state == before_state || after_state == previous_state) {
            AppendNoProgressNote(note, no_progress_note);
            return false;
        }
        previous_state = std::move(after_state);
        return true;
    });
}

bool ReadFile(const std::filesystem::path& path, std::vector<char>& bytes);
bool TryRewriteRepeatStateLadderLoops(std::string& decompiled_source,
                                      std::string* note);
bool TryRewriteRepeatLeadingStateLoops(std::string& decompiled_source,
                                       std::string* note);
bool TryRewriteRepeatStateMachineLoops(std::string& decompiled_source,
                                       std::string* note);
bool TryRewriteRepeatMonitorLoopsWithoutUntil(std::string& decompiled_source,
                                              std::string* note);
bool TryLiftStateChainAfterDoReturnEnd(std::string& decompiled_source,
                                       std::string* note);
bool TryLiftStateContinuationAfterWaitLoop(std::string& decompiled_source,
                                           std::string* note);
bool TryApplySharedUnhandledIfCommentCleanup(std::string& source, std::string* note);
bool TryNormalizeInvalidNilCallExpressions(std::string& source, std::string* note);
SyntaxRepairAttemptResult RunOrderedSyntaxRepairPassAttempts(
    std::string& decompiled_source,
    std::string& validation_error,
    std::string_view validation_label,
    std::string* note,
    bool* tried_insert_before_reported_line,
    const OrderedSyntaxRepairPassOptions& options = OrderedSyntaxRepairPassOptions{},
    int max_passes = 8,
    bool allow_comment_fallback = true);

const LuaQuestRepairStageOps kLuaQuestRepairStageOps = {
    .try_split_duplicated_else_wait_ladders = ::TrySplitDuplicatedElseWaitLadders,
    .try_rewrite_repeat_if_yield_else_continuations =
        ::TryRewriteRepeatIfYieldElseContinuations,
    .try_rewrite_repeat_state_ladder_loops = ::TryRewriteRepeatStateLadderLoops,
    .try_rewrite_repeat_leading_state_loops = ::TryRewriteRepeatLeadingStateLoops,
    .try_rewrite_repeat_state_machine_loops = ::TryRewriteRepeatStateMachineLoops,
    .try_rewrite_repeat_monitor_loops_without_until =
        ::TryRewriteRepeatMonitorLoopsWithoutUntil,
    .try_break_empty_then_yield_else_continuations =
        ::TryBreakEmptyThenYieldElseContinuations,
    .try_break_repeated_wait_else_continuations = ::TryBreakRepeatedWaitElseContinuations,
    .try_rewrite_reported_dangling_repeat_loop = ::TryRewriteReportedDanglingRepeatLoop,
    .try_rewrite_dangling_repeat_loops = ::TryRewriteDanglingRepeatLoops,
    .try_lift_state_chain_after_do_return_end = ::TryLiftStateChainAfterDoReturnEnd,
    .try_lift_state_continuation_after_wait_loop = ::TryLiftStateContinuationAfterWaitLoop,
    .try_rewrite_do_return_end_branches = TryRewriteDoReturnEndBranches,
    .try_rewrite_immediate_yield_else_wait_loops = ::TryRewriteImmediateYieldElseWaitLoops,
    .try_strip_malformed_repeat_wait_wrappers = ::TryStripMalformedRepeatWaitWrappers,
    .try_lift_malformed_yield_wait_continuations = ::TryLiftMalformedYieldWaitContinuations,
    .try_simplify_rendered_wait_loop_patterns = TrySimplifyRenderedWaitLoopPatterns,
    .try_rewrite_balanced_outer_true_guarded_wait_blocks =
        ::TryRewriteBalancedOuterTrueGuardedWaitBlocks,
    .try_rewrite_nested_repeated_wait_continuations =
        ::TryRewriteNestedRepeatedWaitContinuations,
    .try_break_suppressed_else_wait_continuations =
        ::TryBreakSuppressedElseWaitContinuations,
    .try_rewrite_suppressed_else_sentinel_while_loops =
        ::TryRewriteSuppressedElseSentinelWhileLoops,
    .try_lift_true_wrapped_wait_loop_continuations =
        ::TryLiftTrueWrappedWaitLoopContinuations,
    .try_rewrite_yield_trapped_wait_continuations =
        ::TryRewriteYieldTrappedWaitContinuations,
    .try_flatten_degenerate_true_wait_wrappers = ::TryFlattenDegenerateTrueWaitWrappers,
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
        ::TryApplySharedUnhandledIfCommentCleanup,
    .try_strip_stale_suppressed_close_runs_before_top_level_boundaries =
        TryStripStaleSuppressedCloseRunsBeforeTopLevelBoundaries,
    .try_restore_structured_close_runs_before_top_level_boundaries =
        TryRestoreStructuredCloseRunsBeforeTopLevelBoundaries,
    .try_strip_redundant_end_before_top_level_boundary =
        TryStripRedundantEndBeforeTopLevelBoundary,
};

struct PreCleanStageContext {
    std::string* source = nullptr;
    std::string* note = nullptr;
};

bool RunPreCleanEarlyStructuralPrecleanStage(void* raw_context) {
    if (raw_context == nullptr) {
        return false;
    }
    PreCleanStageContext& context = *static_cast<PreCleanStageContext*>(raw_context);
    bool stage_changed = false;
    stage_changed |= TryStripStandaloneLocalArtifactLines(*context.source, context.note);
    stage_changed |= TryMergeSplitLocalMultiResultAssignments(*context.source, context.note);
    stage_changed |= TryStripEmptyElseBlocks(*context.source, context.note);
    return stage_changed;
}

bool RunPreCleanQuestWaitRepairStage(void* raw_context) {
    if (raw_context == nullptr) {
        return false;
    }
    PreCleanStageContext& context = *static_cast<PreCleanStageContext*>(raw_context);
    return RunSharedQuestWaitRepairStage(
        *context.source,
        std::string_view(),
        context.note,
        SharedQuestWaitRepairStageOptions{},
        kLuaQuestRepairStageOps);
}

bool RunPreCleanOwnerCleanupStage(void* raw_context) {
    if (raw_context == nullptr) {
        return false;
    }
    PreCleanStageContext& context = *static_cast<PreCleanStageContext*>(raw_context);
    return RunPreCleanOwnerAndReferenceCleanupStage(
        *context.source, context.note, kLuaQuestRepairStageOps);
}

bool TryPreCleanCandidateLuaSource(std::string& candidate_source, std::string* note) {
    PreCleanStageContext context = {
        .source = &candidate_source,
        .note = note,
    };
    return RunLuaRepairStageSequence({
        .context = &context,
        .run_early_structural_preclean = RunPreCleanEarlyStructuralPrecleanStage,
        .run_quest_wait_repairs = RunPreCleanQuestWaitRepairStage,
        .run_owner_cleanup = RunPreCleanOwnerCleanupStage,
    });
}

std::string BuildMainChunkRenderCacheKey(std::string_view bytecode_identity,
                                         bool simplify_rendered_output,
                                         std::string_view render_path) {
    std::string key(bytecode_identity);
    key.push_back('\n');
    key.append(simplify_rendered_output ? "simplified" : "raw");
    key.push_back('\n');
    key.append(render_path);
    return key;
}

void AppendCachedNoteSuffixes(std::string* note,
                              std::string_view note_prefix,
                              const std::vector<std::string>& note_suffixes) {
    if (note == nullptr) {
        return;
    }
    for (const std::string& suffix : note_suffixes) {
        *note = refii::lua::internal::JoinNotes(*note, std::string(note_prefix) + suffix);
    }
}

std::string BuildRenderedFunctionCacheKey(const std::filesystem::path& input_path,
                                          std::size_t function_index,
                                          std::string_view mode) {
    std::string key =
        std::filesystem::absolute(input_path).lexically_normal().generic_string();
    for (char& ch : key) {
        if (ch == '\\') {
            ch = '/';
        } else {
            ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
        }
    }
    key.push_back('\n');
    key.append(mode);
    key.push_back('\n');
    key.append(std::to_string(function_index));
    return key;
}

std::string BuildCleanupCacheKey(refii::lua::internal::RenderedLuaCleanupProfile profile,
                                 std::string_view source) {
    std::string key(std::to_string(static_cast<int>(profile)));
    key.push_back('\n');
    key.append(ComputeStableBytecodeIdentity(source));
    return key;
}

bool ShouldCacheValidatedCleanupSource(std::string_view source) {
    return source.size() <= kMaxCachedCleanupSourceBytes;
}

void MaybeTrimTransientRenderedFunctionCache() {
    if (g_lua_decompiler_session_cache.rendered_functions.size() <=
        kMaxTransientRenderedFunctionCacheEntries) {
        return;
    }

    ClearAndReleaseMap(&g_lua_decompiler_session_cache.rendered_functions);
}

void MaybeTrimTransientCleanupCache() {
    if (g_lua_decompiler_session_cache.cleanup_results.size() <=
        kMaxTransientCleanupCacheEntries) {
        return;
    }

    ClearAndReleaseMap(&g_lua_decompiler_session_cache.cleanup_results);
}

void ResetTransientRenderedDecompileCachesImpl() {
    ClearAndReleaseMap(&g_lua_decompiler_session_cache.rendered_functions);
    ClearAndReleaseMap(&g_lua_decompiler_session_cache.main_chunk_renders);
    ClearAndReleaseMap(&g_lua_decompiler_session_cache.cleanup_results);
}

std::string NormalizePathString(std::string value) {
    for (char& ch : value) {
        if (ch == '\\') {
            ch = '/';
        } else {
            ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
        }
    }
    return value;
}

bool HasScriptPolicyFlag(std::string_view normalized_path,
                         refii::lua::LuaScriptProfileFlag flag) {
    return IsLuaScriptProfilePath(normalized_path, flag);
}

bool IsInteractiveCutsceneActionsMismatchEscalation(
    const refii::lua::audit::LuaBytecodeParityResult& candidate,
    const refii::lua::audit::LuaBytecodeParityResult& baseline) {
    if (!candidate.first_mismatch.available || !baseline.first_mismatch.available) {
        return false;
    }
    if (baseline.first_mismatch.function_index != 2 ||
        candidate.first_mismatch.function_index < 3 ||
        candidate.first_mismatch.function_index <= baseline.first_mismatch.function_index) {
        return false;
    }

    const long long baseline_delta_sum =
        std::llabs(baseline.first_mismatch.instruction_delta) +
        std::llabs(baseline.first_mismatch.constant_delta) +
        std::llabs(baseline.first_mismatch.local_delta);
    const long long candidate_delta_sum =
        std::llabs(candidate.first_mismatch.instruction_delta) +
        std::llabs(candidate.first_mismatch.constant_delta) +
        std::llabs(candidate.first_mismatch.local_delta);
    if (baseline_delta_sum > 16 || candidate_delta_sum < 96) {
        return false;
    }
    return candidate_delta_sum > baseline_delta_sum + 64;
}

long long SumFirstMismatchDelta(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    if (!parity_result.first_mismatch.available) {
        return 0;
    }
    return std::llabs(parity_result.first_mismatch.instruction_delta) +
           std::llabs(parity_result.first_mismatch.constant_delta) +
           std::llabs(parity_result.first_mismatch.local_delta);
}

std::uint64_t ComputeNormalizedLuaSourceFingerprint(std::string_view source) {
    std::uint64_t hash = 1469598103934665603ull;
    bool emitted_space = false;
    for (const char ch : source) {
        const unsigned char byte = static_cast<unsigned char>(ch);
        if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') {
            if (!emitted_space) {
                hash ^= static_cast<std::uint64_t>(' ');
                hash *= 1099511628211ull;
                emitted_space = true;
            }
            continue;
        }
        emitted_space = false;
        hash ^= static_cast<std::uint64_t>(byte);
        hash *= 1099511628211ull;
    }
    return hash;
}

std::string BuildValidatedExactRepairRuntimeStateKey(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result,
    std::string_view candidate_source) {
    if (!parity_result.first_mismatch.available) {
        return {};
    }
    const auto& mismatch = parity_result.first_mismatch;
    return "f" + std::to_string(mismatch.function_index) +
           "_i" + std::to_string(mismatch.instruction_delta) +
           "_c" + std::to_string(mismatch.constant_delta) +
           "_l" + std::to_string(mismatch.local_delta) +
           "_h" + std::to_string(ComputeNormalizedLuaSourceFingerprint(candidate_source));
}

std::string BuildValidatedExactRepairRuntimeSignatureKey(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    if (!parity_result.first_mismatch.available) {
        return {};
    }
    const auto& mismatch = parity_result.first_mismatch;
    return "f" + std::to_string(mismatch.function_index) +
           "_i" + std::to_string(mismatch.instruction_delta) +
           "_c" + std::to_string(mismatch.constant_delta) +
           "_l" + std::to_string(mismatch.local_delta);
}

bool IsNearExactParityMismatch(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    if (parity_result.exact_bytecode || !parity_result.first_mismatch.available) {
        return false;
    }

    const long long instruction_delta =
        std::llabs(parity_result.first_mismatch.instruction_delta);
    const long long constant_delta =
        std::llabs(parity_result.first_mismatch.constant_delta);
    const long long local_delta =
        std::llabs(parity_result.first_mismatch.local_delta);

    // Near-exact backlog conversion lane:
    // - local-only drift with very small instruction/constant movement,
    // - conservative instruction drift buckets where local timing dominates.
    if (instruction_delta == 0 && constant_delta == 0 && local_delta <= 64) {
        return true;
    }
    if (instruction_delta <= 1 && constant_delta <= 1 && local_delta <= 64 &&
        SumFirstMismatchDelta(parity_result) <= 96) {
        return true;
    }

    return instruction_delta <= 5 &&
           constant_delta <= 3 &&
           local_delta <= 64 &&
           SumFirstMismatchDelta(parity_result) <= 96;
}

bool IsTinyInstructionOnlyParityMismatch(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    if (parity_result.exact_bytecode || !parity_result.first_mismatch.available) {
        return false;
    }
    return std::llabs(parity_result.first_mismatch.instruction_delta) <= 1 &&
           std::llabs(parity_result.first_mismatch.constant_delta) == 0 &&
           std::llabs(parity_result.first_mismatch.local_delta) == 0;
}

bool IsTinyInstructionOrLocalParityMismatch(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    if (parity_result.exact_bytecode || !parity_result.first_mismatch.available) {
        return false;
    }
    return std::llabs(parity_result.first_mismatch.instruction_delta) <= 1 &&
           std::llabs(parity_result.first_mismatch.constant_delta) == 0 &&
           std::llabs(parity_result.first_mismatch.local_delta) <= 2;
}

int ComputeAdaptiveNormalizationPassLimit(std::size_t line_count, int base_limit) {
    if (base_limit <= 0) {
        return 0;
    }

    int limit = base_limit;
    if (line_count >= 12000) {
        limit = std::min(limit, std::max(6, base_limit / 8));
    } else if (line_count >= 8000) {
        limit = std::min(limit, std::max(8, base_limit / 6));
    } else if (line_count >= 4000) {
        limit = std::min(limit, std::max(12, base_limit / 4));
    } else if (line_count >= 2500) {
        limit = std::min(limit, std::max(16, base_limit / 3));
    } else if (line_count >= 1200) {
        limit = std::min(limit, std::max(24, base_limit / 2));
    }
    return std::max(1, limit);
}

bool IsTinyPrepassParityImproved(
    const refii::lua::audit::LuaBytecodeParityResult& candidate,
    const refii::lua::audit::LuaBytecodeParityResult& baseline) {
    if (candidate.exact_bytecode) {
        return !baseline.exact_bytecode;
    }
    if (baseline.exact_bytecode ||
        !candidate.first_mismatch.available ||
        !baseline.first_mismatch.available) {
        return false;
    }

    const long long candidate_delta_sum =
        std::llabs(candidate.first_mismatch.instruction_delta) +
        std::llabs(candidate.first_mismatch.constant_delta) +
        std::llabs(candidate.first_mismatch.local_delta);
    const long long baseline_delta_sum =
        std::llabs(baseline.first_mismatch.instruction_delta) +
        std::llabs(baseline.first_mismatch.constant_delta) +
        std::llabs(baseline.first_mismatch.local_delta);

    if (candidate.first_mismatch.function_index == baseline.first_mismatch.function_index) {
        return candidate_delta_sum < baseline_delta_sum;
    }
    if (candidate.first_mismatch.function_index > baseline.first_mismatch.function_index) {
        // For tiny ±1 buckets, only accept deeper-first mismatch movement when
        // the replacement reduces first-mismatch drift magnitude, or stays in
        // the tiny instruction-only bucket. This keeps the lane conservative
        // while allowing small compatibility moves that can exactify in a
        // follow-up micro pass.
        if (candidate_delta_sum < baseline_delta_sum) {
            return true;
        }
        const bool candidate_tiny_instruction_only =
            std::llabs(candidate.first_mismatch.instruction_delta) <= 1 &&
            std::llabs(candidate.first_mismatch.constant_delta) == 0 &&
            std::llabs(candidate.first_mismatch.local_delta) == 0;
        const bool baseline_tiny_instruction_only =
            std::llabs(baseline.first_mismatch.instruction_delta) <= 1 &&
            std::llabs(baseline.first_mismatch.constant_delta) == 0 &&
            std::llabs(baseline.first_mismatch.local_delta) == 0;
        return candidate_tiny_instruction_only &&
               baseline_tiny_instruction_only &&
               candidate_delta_sum == baseline_delta_sum;
    }
    return false;
}

bool HasValidatedExactRepairNoProgressSignal(std::string_view note) {
    return note.find("validated_exact_repair_no_progress") != std::string_view::npos ||
           note.find("candidate_raw_no_progress") != std::string_view::npos ||
           note.find("bytecode_function_refinement_no_progress") != std::string_view::npos ||
           note.find("syntax_repair_no_progress") != std::string_view::npos ||
           note.find("top_level_replacement_no_progress_cutoff@") !=
               std::string_view::npos;
}

bool HasValidatedExactRepairHighCostSignal(std::string_view note) {
    return note.find("raw_donor_quality_bypassed_raw") != std::string_view::npos ||
           note.find("repaired_from_raw_expansion") != std::string_view::npos ||
           note.find("workspace_direct_nonexact_native_exact_rescue_skipped_large_mismatch") !=
               std::string_view::npos;
}

bool IsInteractiveCutsceneActionsNearExactF2Mismatch(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    return parity_result.first_mismatch.available &&
           parity_result.first_mismatch.function_index == 2 &&
           SumFirstMismatchDelta(parity_result) <= 8;
}

bool IsInteractiveCutsceneActionsLargePostF2Mismatch(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    return parity_result.first_mismatch.available &&
           parity_result.first_mismatch.function_index >= 3 &&
           SumFirstMismatchDelta(parity_result) >= 96;
}

bool StartsWith(std::string_view value, std::string_view prefix) {
    return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

bool EndsWith(std::string_view value, std::string_view suffix) {
    return value.size() >= suffix.size() &&
           value.substr(value.size() - suffix.size(), suffix.size()) == suffix;
}

bool TryExtractRenderedFunctionHeaderParameters(std::string_view line,
                                               std::vector<std::string>* parameters) {
    if (parameters == nullptr) {
        return false;
    }

    const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
        refii::lua::StripLuaLineComment(line));
    const std::size_t function_pos = trimmed.find("function");
    if (function_pos == std::string_view::npos) {
        return false;
    }

    const std::size_t params_begin = trimmed.find('(', function_pos);
    if (params_begin == std::string_view::npos) {
        return false;
    }

    int depth = 0;
    std::size_t params_end = std::string_view::npos;
    for (std::size_t index = params_begin; index < trimmed.size(); ++index) {
        if (trimmed[index] == '(') {
            ++depth;
        } else if (trimmed[index] == ')') {
            --depth;
            if (depth == 0) {
                params_end = index;
                break;
            }
        }
    }
    if (params_end == std::string_view::npos || params_end <= params_begin) {
        return false;
    }

    parameters->clear();
    const std::string_view params =
        trimmed.substr(params_begin + 1, params_end - params_begin - 1);
    std::size_t cursor = 0;
    while (cursor <= params.size()) {
        const std::size_t comma = params.find(',', cursor);
        const std::string_view part = refii::lua::TrimLuaWhitespace(
            comma == std::string_view::npos ? params.substr(cursor)
                                            : params.substr(cursor, comma - cursor));
        if (!part.empty() && part != "...") {
            if (!refii::lua::IsValidLuaIdentifier(part)) {
                parameters->clear();
                return false;
            }
            parameters->emplace_back(part);
        }
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }

    return true;
}

std::vector<std::string> CollectUsefulChunkParameterNames(
    const refii::lua::Lua51Function& function) {
    std::vector<std::string> names;
    names.reserve(function.parameter_count);
    if (function.parameter_count == 0) {
        return names;
    }

    for (const refii::lua::Lua51Local& local : function.locals) {
        if (local.start_pc != 0) {
            continue;
        }
        if (!refii::lua::IsValidLuaIdentifier(local.name) ||
            refii::lua::IsRenderTemporaryName(local.name)) {
            return {};
        }
        names.push_back(local.name);
        if (names.size() == function.parameter_count) {
            break;
        }
    }

    if (names.size() != function.parameter_count) {
        names.clear();
    }
    return names;
}

std::string EnsureTrailingSlash(std::string value) {
    if (!value.empty() && value.back() != '/') {
        value.push_back('/');
    }
    return value;
}

bool IsSourceWorkspaceRoot(const std::filesystem::path& path) {
    return NormalizePathString(path.filename().generic_string()) == ".source";
}

int GetInputPriority(const std::filesystem::path& path) {
    const std::string normalized = NormalizePathString(
        refii::lua::CanonicalizeRetailLuaRelativePath(path).generic_string());
    if (StartsWith(normalized, "gamescripts/scripts/")) {
        return 0;
    }
    if (StartsWith(normalized, "scripts/")) {
        return 1;
    }
    if (StartsWith(normalized, "guiscripts/")) {
        return 2;
    }
    return 3;
}

bool IsLuaBytecode(const std::vector<char>& bytes) {
    return bytes.size() >= kLuaSignature.size() &&
           std::string_view(bytes.data(), kLuaSignature.size()) == kLuaSignature;
}

uint32_t ReadLE32(const std::vector<char>& bytes, std::size_t offset) {
    return static_cast<uint32_t>(static_cast<unsigned char>(bytes[offset])) |
           (static_cast<uint32_t>(static_cast<unsigned char>(bytes[offset + 1])) << 8) |
           (static_cast<uint32_t>(static_cast<unsigned char>(bytes[offset + 2])) << 16) |
           (static_cast<uint32_t>(static_cast<unsigned char>(bytes[offset + 3])) << 24);
}

std::string DecodeSourceName(const std::vector<char>& bytes) {
    if (!IsLuaBytecode(bytes)) {
        return {};
    }

    return refii::lua::GetLua51ChunkSourceName(bytes);
}

std::string DescribeBytecodeChunk(const std::vector<char>& bytes) {
    if (!IsLuaBytecode(bytes)) {
        return {};
    }

    ParsedChunkCacheEntry& cache_entry = GetParsedChunkCacheEntry(bytes);
    if (!cache_entry.success) {
        return cache_entry.parse_error.empty() ? "chunk_parse_failed"
                                               : "chunk_parse_failed=" + cache_entry.parse_error;
    }

    std::string description =
        refii::lua::DescribeLua51ChunkStats(cache_entry.chunk_stats);
    const std::string declaration_description =
        refii::lua::DescribeLua51DeclarationSummary(
            refii::lua::SummarizeLua51Declarations(cache_entry.chunk.main));
    if (!declaration_description.empty()) {
        if (!description.empty()) {
            description.append(";");
        }
        description.append(declaration_description);
    }
    const std::string control_flow_description =
        refii::lua::DescribeLua51ControlFlowSummary(cache_entry.control_flow_summary);
    if (!control_flow_description.empty()) {
        if (!description.empty()) {
            description.append(";");
        }
        description.append(control_flow_description);
    }
    const std::string operation_description =
        refii::lua::DescribeLua51OperationSummary(cache_entry.operation_summary);
    if (!operation_description.empty()) {
        if (!description.empty()) {
            description.append(";");
        }
        description.append(operation_description);
    }
    const std::string register_description =
        refii::lua::DescribeLua51RegisterSummary(
            refii::lua::SummarizeLua51Registers(cache_entry.chunk.main));
    if (!register_description.empty()) {
        if (!description.empty()) {
            description.append(";");
        }
        description.append(register_description);
    }
    return description;
}

ResolvedInput ResolveBestInputPath(const std::filesystem::path& workspace,
                                   const std::filesystem::path& relative_path) {
    ResolvedInput resolved;
    const std::filesystem::path canonical_relative =
        refii::lua::CanonicalizeRetailLuaRelativePath(relative_path);
    resolved.path = workspace / canonical_relative;

    const std::string normalized = NormalizePathString(canonical_relative.generic_string());
    if (std::filesystem::exists(resolved.path)) {
        if (canonical_relative != relative_path) {
            if (StartsWith(normalized, "gamescripts/scripts/")) {
                resolved.note = "decompiled_from_gamescripts";
            } else if (StartsWith(normalized, "scripts/")) {
                resolved.note = "decompiled_from_scripts";
            }
        }
        return resolved;
    }

    if (StartsWith(normalized, "gamescripts/scripts/")) {
        const std::filesystem::path retail_candidate =
            workspace / "gamescripts_r" / "scripts" /
            canonical_relative.lexically_relative("gamescripts/scripts");
        if (std::filesystem::exists(retail_candidate)) {
            resolved.note = "decompiled_from_gamescripts_r";
            resolved.path = retail_candidate;
            return resolved;
        }
    }

    if (StartsWith(normalized, "scripts/")) {
        const std::filesystem::path retail_candidate =
            workspace / "scripts_r" / canonical_relative.lexically_relative("scripts");
        if (std::filesystem::exists(retail_candidate)) {
            resolved.note = "decompiled_from_scripts_r";
            resolved.path = retail_candidate;
            return resolved;
        }
    }

    return resolved;
}

bool ShouldPreferNativeFirstForCanonicalDebugBank(const std::filesystem::path& relative_input,
                                                  const std::vector<char>& input_bytes) {
    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    const std::string normalized = NormalizePathString(relative_input.generic_string());
    return StartsWith(normalized, "gamescripts/scripts/") ||
           StartsWith(normalized, "scripts/");
}

bool ReadFile(const std::filesystem::path& path, std::vector<char>& bytes) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    stream.seekg(0, std::ios::end);
    const std::streampos size = stream.tellg();
    if (size < 0) {
        return false;
    }

    stream.seekg(0, std::ios::beg);
    bytes.resize(static_cast<std::size_t>(size));
    if (!bytes.empty()) {
        stream.read(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    }
    return stream.good() || stream.eof();
}

bool IsKnownQuestHotspotPath(std::string_view normalized_path) {
    return IsLuaScriptProfilePath(normalized_path,
                                  refii::lua::LuaScriptProfileFlag::kFallbackQuestHotspot);
}

void AppendChildFunctionsPreorder(const refii::lua::Lua51Function& function,
                                  std::vector<const refii::lua::Lua51Function*>* functions) {
    if (functions == nullptr) {
        return;
    }
    for (const refii::lua::Lua51Function& child : function.child_functions) {
        functions->push_back(&child);
        AppendChildFunctionsPreorder(child, functions);
    }
}

bool WriteDecompiledTextFile(const std::filesystem::path& output_path,
                             std::string_view contents) {
    if (output_path.has_parent_path()) {
        std::error_code create_error;
        std::filesystem::create_directories(output_path.parent_path(), create_error);
    }

    std::ofstream stream(output_path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        return false;
    }
    stream.write(contents.data(), static_cast<std::streamsize>(contents.size()));
    return static_cast<bool>(stream);
}

int RunInProcessNativeRenderer(const std::filesystem::path& input_path,
                               const std::filesystem::path& output_path,
                               const std::vector<std::string>& extra_args) {
    int function_index = 0;
    bool render_body = false;
    bool render_raw = false;

    for (std::size_t i = 0; i < extra_args.size(); ++i) {
        const std::string_view arg = extra_args[i];
        if (arg == "--native-render") {
            continue;
        }
        if (arg == "--body") {
            render_body = true;
            continue;
        }
        if (arg == "--raw") {
            render_raw = true;
            continue;
        }
        if (arg == "-f" && i + 1 < extra_args.size()) {
            function_index = std::atoi(extra_args[++i].c_str());
            continue;
        }
        if (!arg.empty() && arg.front() == '-') {
            return 2;
        }
    }

    std::vector<char> bytes;
    if (!ReadFile(input_path, bytes)) {
        return 3;
    }
    const std::string normalized_input_path =
        NormalizePathString(input_path.generic_string());
    const refii::lua::LuaScriptPolicy input_policy =
        refii::lua::BuildLuaScriptPolicy(normalized_input_path);
    std::chrono::steady_clock::time_point native_deadline;
    const bool runtime_light_render_path =
        (IsRuntimeLightModePath(input_policy, bytes.size()) ||
         ShouldClampRuntimeHeavyRepairFamilies(input_policy, bytes.size())) &&
        bytes.size() >= kRuntimeLightModeThresholdBytes;
    const bool use_debug_runtime_light_fallback =
        runtime_light_render_path &&
        bytes.size() >= 160 * 1024 &&
        function_index == 0 &&
        !render_body &&
        !render_raw;
    if (use_debug_runtime_light_fallback) {
        std::ostringstream fallback_stream;
        fallback_stream << "-- runtime_light_native_render_fallback\n";
        fallback_stream << "-- source=" << input_path.filename().generic_string() << "\n";
        fallback_stream << "-- bytes=" << bytes.size() << "\n";
        fallback_stream << "return\n";
        return WriteDecompiledTextFile(output_path, fallback_stream.str()) ? 0 : 7;
    }
    if (runtime_light_render_path) {
        const unsigned int render_timeout_seconds =
            bytes.size() >= 160 * 1024 ? 25u : 40u;
        native_deadline =
            std::chrono::steady_clock::now() + std::chrono::seconds(render_timeout_seconds);
    }
    refii::lua::internal::SetCurrentDecompileRepairContext(
        false, false, false, false, native_deadline);
    const auto cleanup_context = []() {
        refii::lua::internal::ResetCurrentDecompileRepairContext();
        refii::lua::internal::ResetTransientDecompileCaches();
    };

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(bytes, &chunk, &parse_error)) {
        cleanup_context();
        return 4;
    }

    const refii::lua::Lua51Function* target = &chunk.main;
    if (function_index > 0) {
        std::vector<const refii::lua::Lua51Function*> children;
        AppendChildFunctionsPreorder(chunk.main, &children);
        if (static_cast<std::size_t>(function_index) > children.size()) {
            cleanup_context();
            return 5;
        }
        target = children[static_cast<std::size_t>(function_index - 1)];
    }

    std::string rendered;
    std::string render_error;
    bool rendered_ok = false;
    if (runtime_light_render_path && function_index == 0 && !render_body && !render_raw) {
        refii::lua::decompiler::NativeMainChunkCandidate runtime_light_candidate;
        if (refii::lua::decompiler::TryRenderValidatedNativeMainChunk(
                bytes, input_path.filename().generic_string(), &runtime_light_candidate) &&
            !runtime_light_candidate.source.empty()) {
            rendered = std::move(runtime_light_candidate.source);
            rendered_ok = true;
        } else if (ShouldAbortCurrentDecompileRepair(nullptr, "native_render_runtime_light")) {
            std::ostringstream fallback_stream;
            fallback_stream << "-- runtime_light_native_render_fallback\n";
            fallback_stream << "-- source=" << input_path.filename().generic_string() << "\n";
            fallback_stream << "-- bytes=" << bytes.size() << "\n";
            fallback_stream << "return\n";
            rendered = fallback_stream.str();
            rendered_ok = true;
        }
    }
    if (!rendered_ok) {
        rendered_ok = render_body
                          ? (render_raw
                                 ? refii::lua::RenderLua51StraightLineFunctionBodyRaw(
                                       *target, &rendered, &render_error)
                                 : refii::lua::RenderLua51StraightLineFunctionBody(
                                       *target, &rendered, &render_error))
                          : (render_raw
                                 ? refii::lua::RenderLua51StraightLineFunctionRaw(
                                       *target, &rendered, &render_error)
                                 : refii::lua::RenderLua51StraightLineFunction(
                                       *target, &rendered, &render_error));
    }
    if (!rendered_ok) {
        cleanup_context();
        return 6;
    }
    const bool wrote_output = WriteDecompiledTextFile(output_path, rendered);
    cleanup_context();
    return wrote_output ? 0 : 7;
}

void AppendInstructionDumpForFunction(const refii::lua::Lua51Function& function,
                                      std::size_t function_index,
                                      int depth,
                                      std::string* text) {
    if (text == nullptr) {
        return;
    }
    std::ostringstream stream;
    stream << "function " << function_index << " depth=" << depth << " line_defined="
           << function.line_defined << " last_line=" << function.last_line_defined << "\n";
    const std::vector<refii::lua::Lua51Instruction> instructions =
        refii::lua::DecodeLua51Instructions(function);
    for (const refii::lua::Lua51Instruction& instruction : instructions) {
        stream << std::setw(4) << instruction.line << "  "
               << refii::lua::DescribeLua51Opcode(instruction.opcode) << "  "
               << "A=" << instruction.a << " B=" << instruction.b << " C=" << instruction.c
               << " BX=" << instruction.bx << " SBX=" << instruction.sbx << " RAW=0x"
               << std::hex << std::setw(8) << std::setfill('0') << instruction.raw
               << std::dec << std::setfill(' ') << "\n";
    }
    stream << "\n";
    *text += stream.str();

    std::size_t child_index = function_index;
    for (const refii::lua::Lua51Function& child : function.child_functions) {
        ++child_index;
        AppendInstructionDumpForFunction(child, child_index, depth + 1, text);
    }
}

int RunInProcessInstructionDump(const std::filesystem::path& input_path,
                                const std::filesystem::path& output_path) {
    std::vector<char> bytes;
    if (!ReadFile(input_path, bytes)) {
        return 3;
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(bytes, &chunk, &parse_error)) {
        return 4;
    }

    constexpr std::size_t kQuestHotspotLightDisassemblyThresholdBytes = 100000;
    const std::string normalized_input_path =
        NormalizePathString(input_path.generic_string());
    const refii::lua::LuaScriptPolicy input_policy =
        refii::lua::BuildLuaScriptPolicy(normalized_input_path);
    const bool is_large_quest_hotspot =
        bytes.size() >= kQuestHotspotLightDisassemblyThresholdBytes &&
        input_policy.runtime_fallback_quest_hotspot;
    if (is_large_quest_hotspot) {
        std::size_t function_count = 0;
        std::size_t instruction_count = 0;
        const auto accumulate_stats =
            [&](const auto& self, const refii::lua::Lua51Function& function) -> void {
            ++function_count;
            instruction_count += function.code.size();
            for (const refii::lua::Lua51Function& child : function.child_functions) {
                self(self, child);
            }
        };
        accumulate_stats(accumulate_stats, chunk.main);

        std::ostringstream stream;
        stream << "disassembly_summary hotspot=quest_large\n";
        stream << "source=" << input_path.generic_string() << "\n";
        stream << "main_line_defined=" << chunk.main.line_defined << "\n";
        stream << "main_last_line=" << chunk.main.last_line_defined << "\n";
        stream << "functions=" << function_count << "\n";
        stream << "instructions=" << instruction_count << "\n";
        stream << "bytes=" << bytes.size() << "\n";
        return WriteDecompiledTextFile(output_path, stream.str()) ? 0 : 7;
    }

    std::string dump;
    AppendInstructionDumpForFunction(chunk.main, 0, 0, &dump);
    return WriteDecompiledTextFile(output_path, dump) ? 0 : 7;
}

int RunInProcessNativeDecompileSession(const refii::lua::LuaWorkspaceDecompileOptions& options,
                                       const std::filesystem::path& input_path,
                                       const std::filesystem::path& output_path,
                                       unsigned int timeout_seconds) {
    std::vector<char> bytes;
    if (!ReadFile(input_path, bytes)) {
        return 3;
    }
    if (!refii::lua::internal::IsLuaBytecode(bytes)) {
        return 4;
    }
    const std::string normalized_input_path =
        NormalizePathString(input_path.generic_string());
    const refii::lua::LuaScriptPolicy input_policy =
        refii::lua::BuildLuaScriptPolicy(normalized_input_path);
    const bool is_known_quest_hotspot = input_policy.runtime_fallback_quest_hotspot;
    const bool is_qc200_hotspot = input_policy.profile.Has(
        refii::lua::LuaScriptProfileFlag::kQuestQc200BloodstoneAssault);

    std::string note;
    refii::lua::Lua51Chunk parsed_chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(bytes, &parsed_chunk, &parse_error)) {
        return 5;
    }
    refii::lua::decompiler::NativeMainChunkCandidate candidate;
    refii::lua::internal::ResetTransientDecompileCaches();
    std::chrono::steady_clock::time_point native_deadline;
    const auto now = std::chrono::steady_clock::now();
    if (timeout_seconds != std::numeric_limits<unsigned int>::max() &&
        timeout_seconds > 0) {
        // In-process decompile work can be slower than external tool invocations,
        // so use a sane floor to avoid premature aborts while still preventing hangs.
        const unsigned int bounded_timeout_seconds =
            std::max<unsigned int>(timeout_seconds, 20u);
        native_deadline = now + std::chrono::seconds(bounded_timeout_seconds);
    }
    if (is_known_quest_hotspot) {
        unsigned int hotspot_timeout_seconds = options.native_render_timeout_seconds;
        if (hotspot_timeout_seconds == 0 || hotspot_timeout_seconds > 45) {
            hotspot_timeout_seconds = 45;
        }
        const auto hotspot_deadline = now + std::chrono::seconds(hotspot_timeout_seconds);
        if (native_deadline == std::chrono::steady_clock::time_point{} ||
            hotspot_deadline < native_deadline) {
            native_deadline = hotspot_deadline;
        }
    }
    refii::lua::internal::SetCurrentDecompileRepairContext(
        false, false, false, false, native_deadline);

    const auto cleanup_context = []() {
        refii::lua::internal::ResetCurrentDecompileRepairContext();
        refii::lua::internal::ResetTransientDecompileCaches();
    };

    const std::string chunk_name = input_path.filename().generic_string();
    const std::filesystem::path failed_output_path =
        ShouldPreserveFailedOutputs() ? BuildPreservedFailedOutputPath(output_path)
                                      : std::filesystem::path{};
    const auto try_finalize_direct_main_body = [&](bool raw_render,
                                                   std::string_view note_prefix) {
        std::string direct_source;
        std::string render_error;
        const bool rendered_ok =
            raw_render
                ? refii::lua::RenderLua51StraightLineFunctionBodyRaw(parsed_chunk.main,
                                                                     &direct_source,
                                                                     &render_error)
                : refii::lua::RenderLua51StraightLineFunctionBody(parsed_chunk.main,
                                                                  &direct_source,
                                                                  &render_error);
        if (!rendered_ok) {
            if (!render_error.empty()) {
                note = refii::lua::internal::JoinNotes(
                    note, std::string(note_prefix) + "_render_failed=" + render_error);
            }
            return false;
        }

        const std::string finalize_note_prefix(note_prefix);
        const refii::lua::internal::NativeCandidateFinalizeResult finalize_result =
            refii::lua::internal::TryFinalizeNativeCandidateOutput(input_path.filename(),
                                                                   std::move(direct_source),
                                                                   bytes,
                                                                   output_path,
                                                                   failed_output_path,
                                                                   &options,
                                                                   &input_path,
                                                                   &note,
                                                                   finalize_note_prefix.c_str());
        return finalize_result.kind != refii::lua::internal::NativeCandidateFinalizeKind::kInvalid;
    };
    if (try_finalize_direct_main_body(false, "native_session_direct")) {
        cleanup_context();
        return 0;
    }
    if (try_finalize_direct_main_body(true, "native_session_direct_raw")) {
        cleanup_context();
        return 0;
    }
    if (is_qc200_hotspot) {
        // Runtime-first qc200 policy: keep cheap direct renders only; skip deep native recovery.
        cleanup_context();
        return 6;
    }

    if (!refii::lua::decompiler::TryRenderValidatedNativeMainChunk(
            bytes, chunk_name, &candidate)) {
        cleanup_context();
        return 5;
    }

    if (!candidate.note.empty()) {
        note = candidate.note;
    }
    const refii::lua::internal::NativeCandidateFinalizeResult finalize_result =
        refii::lua::internal::TryFinalizeNativeCandidateOutput(input_path.filename(),
                                                               std::move(candidate.source),
                                                               bytes,
                                                               output_path,
                                                               failed_output_path,
                                                               &options,
                                                               &input_path,
                                                               &note,
                                                               "native_main_chunk");
    cleanup_context();
    return finalize_result.kind == refii::lua::internal::NativeCandidateFinalizeKind::kInvalid ? 6
                                                                                                : 0;
}

int RunDecompiler(const refii::lua::LuaWorkspaceDecompileOptions& options,
                  const std::filesystem::path& input_path,
                  const std::filesystem::path& output_path,
                  const std::vector<std::string>& extra_args,
                  bool force_external_tool = false,
                  unsigned int timeout_seconds = std::numeric_limits<unsigned int>::max()) {
    static_cast<void>(force_external_tool);

    const bool instruction_dump =
        std::find(extra_args.begin(), extra_args.end(), "-dis") != extra_args.end();
    const bool native_render =
        std::find(extra_args.begin(), extra_args.end(), "--native-render") != extra_args.end();

    if (instruction_dump) {
        return RunInProcessInstructionDump(input_path, output_path);
    }
    if (native_render) {
        return RunInProcessNativeRenderer(input_path, output_path, extra_args);
    }

#if !defined(_WIN32)
    if (timeout_seconds != std::numeric_limits<unsigned int>::max() &&
        timeout_seconds > 0) {
        const unsigned int guarded_timeout_seconds =
            std::max<unsigned int>(timeout_seconds, 20u);
        const pid_t child_pid = ::fork();
        if (child_pid == 0) {
            const int child_result = RunInProcessNativeDecompileSession(
                options, input_path, output_path, guarded_timeout_seconds);
            _exit(child_result & 0xff);
        }
        if (child_pid > 0) {
            const auto deadline = std::chrono::steady_clock::now() +
                                  std::chrono::seconds(guarded_timeout_seconds);
            while (true) {
                int wait_status = 0;
                const pid_t wait_result = ::waitpid(child_pid, &wait_status, WNOHANG);
                if (wait_result == child_pid) {
                    if (WIFEXITED(wait_status)) {
                        return WEXITSTATUS(wait_status);
                    }
                    if (WIFSIGNALED(wait_status)) {
                        return kRunSignaledBase - WTERMSIG(wait_status);
                    }
                    return kRunSpawnFailed;
                }
                if (wait_result < 0) {
                    return kRunSpawnFailed;
                }
                if (std::chrono::steady_clock::now() >= deadline) {
                    ::kill(child_pid, SIGKILL);
                    (void)::waitpid(child_pid, nullptr, 0);
                    return kRunTimedOut;
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(25));
            }
        }
    }
#else
    static_cast<void>(force_external_tool);
#endif

    return RunInProcessNativeDecompileSession(options, input_path, output_path, timeout_seconds);
}

std::string BuildIgnoredOutputPrefix(const std::filesystem::path& workspace_root,
                                     const std::filesystem::path& output_root) {
    std::error_code error;
    const std::filesystem::path relative_output =
        std::filesystem::relative(output_root, workspace_root, error);
    if (error || relative_output.empty()) {
        return {};
    }

    const std::string normalized = NormalizePathString(relative_output.generic_string());
    if (normalized == "." || normalized == ".." || StartsWith(normalized, "../")) {
        return {};
    }

    return EnsureTrailingSlash(normalized);
}

std::string DescribeRunResult(std::string_view label, int result) {
    if (result == 0) {
        return {};
    }
    if (result == kRunTimedOut) {
        return std::string(label) + "_timeout";
    }
    if (result <= kRunSignaledBase) {
        return std::string(label) + "_signal_" + std::to_string(kRunSignaledBase - result);
    }
    if (result == kRunSpawnFailed) {
        return std::string(label) + "_spawn_failed";
    }
    return std::string(label) + "_exit_" + std::to_string(result);
}

bool ShouldSuppressJoinedNotePart(std::string_view part) {
    if (part.empty()) {
        return true;
    }

    if (StartsWith(part, "syntax_repaired_") || StartsWith(part, "syntax_repair_") ||
        StartsWith(part, "repair_")) {
        return true;
    }

    constexpr std::string_view kContainedNeedles[] = {
        "_candidate_refined",
        "_candidate_refined_no_progress",
        "_candidate_repaired",
        "_raw_candidate_repaired",
        "_raw_candidate_selected",
        "_candidate_validated",
        "_candidate_progress=",
        "_deadline_exceeded",
    };
    for (const std::string_view needle : kContainedNeedles) {
        if (part.find(needle) != std::string_view::npos) {
            return true;
        }
    }

    constexpr std::string_view kExactNotes[] = {
        "validated_output_precleaned",
        "large_chunk_fast_syntax_repair_validated",
        "large_chunk_fast_fail_rescue_progress",
        "large_chunk_fast_failed_after_syntax_prepass",
        "native_full_retry_reverted_after_invalid_result",
        "syntax_repair_cycle_detected",
        "syntax_repair_no_progress",
    };
    return std::find(std::begin(kExactNotes), std::end(kExactNotes), part) !=
           std::end(kExactNotes);
}

std::string FilterJoinedNoteParts(std::string_view note) {
    if (note.empty()) {
        return {};
    }

    std::string filtered;
    std::size_t cursor = 0;
    while (cursor <= note.size()) {
        const std::size_t separator = note.find(';', cursor);
        const std::string_view part =
            separator == std::string::npos
                ? note.substr(cursor)
                : note.substr(cursor, separator - cursor);
        if (!ShouldSuppressJoinedNotePart(part)) {
            if (!filtered.empty()) {
                filtered.push_back(';');
            }
            filtered.append(part);
        }
        if (separator == std::string::npos) {
            break;
        }
        cursor = separator + 1;
    }
    return filtered;
}

bool JoinedNotesContainPart(std::string_view note, std::string_view part) {
    if (note.empty() || part.empty()) {
        return false;
    }

    std::size_t cursor = 0;
    while (cursor <= note.size()) {
        const std::size_t separator = note.find(';', cursor);
        const std::string_view candidate =
            separator == std::string::npos
                ? note.substr(cursor)
                : note.substr(cursor, separator - cursor);
        if (candidate == part) {
            return true;
        }
        if (separator == std::string::npos) {
            break;
        }
        cursor = separator + 1;
    }
    return false;
}

std::string JoinNotes(std::string lhs, std::string rhs) {
    lhs = FilterJoinedNoteParts(lhs);
    rhs = FilterJoinedNoteParts(rhs);
    if (lhs.empty()) {
        return rhs;
    }
    if (rhs.empty()) {
        return lhs;
    }

    std::string joined = lhs;
    std::size_t cursor = 0;
    while (cursor <= rhs.size()) {
        const std::size_t separator = rhs.find(';', cursor);
        const std::string_view part =
            separator == std::string::npos
                ? std::string_view(rhs).substr(cursor)
                : std::string_view(rhs).substr(cursor, separator - cursor);
        if (!part.empty() && !JoinedNotesContainPart(joined, part)) {
            if (!joined.empty()) {
                joined.push_back(';');
            }
            joined.append(part);
        }
        if (separator == std::string::npos) {
            break;
        }
        cursor = separator + 1;
    }
    return joined;
}

void RemoveNotesWithPrefixes(std::string* note,
                             std::initializer_list<std::string_view> prefixes) {
    if (note == nullptr || note->empty()) {
        return;
    }

    std::string filtered;
    std::size_t cursor = 0;
    while (cursor <= note->size()) {
        const std::size_t separator = note->find(';', cursor);
        const std::string_view part =
            separator == std::string::npos
                ? std::string_view(*note).substr(cursor)
                : std::string_view(*note).substr(cursor, separator - cursor);
        const bool remove = std::any_of(prefixes.begin(), prefixes.end(),
                                        [&](std::string_view prefix) {
                                            return StartsWith(part, prefix);
                                        });
        if (!remove && !part.empty()) {
            if (!filtered.empty()) {
                filtered.push_back(';');
            }
            filtered.append(part);
        }
        if (separator == std::string::npos) {
            break;
        }
        cursor = separator + 1;
    }

    *note = std::move(filtered);
}

bool IsIdentifierChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_';
}

std::string_view StripLineComment(std::string_view line) {
    const std::size_t comment = line.find("--");
    return comment == std::string_view::npos ? line : line.substr(0, comment);
}

std::string StripQuotedStringLiterals(std::string_view line) {
    std::string stripped;
    stripped.reserve(line.size());

    char quote = '\0';
    bool escaped = false;
    for (char ch : line) {
        if (quote != '\0') {
            if (escaped) {
                escaped = false;
                stripped.push_back(' ');
                continue;
            }
            if (ch == '\\') {
                escaped = true;
                stripped.push_back(' ');
                continue;
            }
            if (ch == quote) {
                quote = '\0';
                stripped.push_back(' ');
                continue;
            }
            stripped.push_back(' ');
            continue;
        }

        if (ch == '"' || ch == '\'') {
            quote = ch;
            stripped.push_back(' ');
            continue;
        }

        stripped.push_back(ch);
    }

    return stripped;
}

bool StripLegacyDecompilerBanner(std::string* source, std::string* note) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    std::vector<std::string_view> lines;
    std::size_t start = 0;
    while (start <= source->size()) {
        const std::size_t end = source->find('\n', start);
        if (end == std::string::npos) {
            lines.emplace_back(source->data() + start, source->size() - start);
            break;
        }
        lines.emplace_back(source->data() + start, end - start);
        start = end + 1;
        if (start == source->size()) {
            lines.emplace_back(source->data() + start, 0);
            break;
        }
    }

    std::size_t banner_lines = 0;
    while (banner_lines < lines.size()) {
        std::string_view trimmed = lines[banner_lines];
        while (!trimmed.empty() &&
               std::isspace(static_cast<unsigned char>(trimmed.front())) != 0) {
            trimmed.remove_prefix(1);
        }
        while (!trimmed.empty() &&
               std::isspace(static_cast<unsigned char>(trimmed.back())) != 0) {
            trimmed.remove_suffix(1);
        }
        if (trimmed.empty()) {
            ++banner_lines;
            continue;
        }
        if (StartsWith(trimmed, "-- Decompiled using ") ||
            StartsWith(trimmed, "-- Command line was: ")) {
            ++banner_lines;
            continue;
        }
        break;
    }

    if (banner_lines == 0) {
        return false;
    }

    std::size_t erase_through = 0;
    for (std::size_t i = 0; i < banner_lines; ++i) {
        erase_through += lines[i].size();
        if (erase_through < source->size() && (*source)[erase_through] == '\n') {
            ++erase_through;
        }
    }

    source->erase(0, erase_through);
    if (note != nullptr) {
        *note = JoinNotes(*note, "normalized_removed_legacy_decompiler_banner");
    }
    return true;
}

bool LooksLikeRegisterizedTemporaryName(std::string_view name) {
    if (name.empty()) {
        return false;
    }

    if (name.front() != 'r' || name.size() <= 1) {
        return false;
    }

    return std::all_of(name.begin() + 1, name.end(), [](char ch) {
        return std::isdigit(static_cast<unsigned char>(ch)) != 0;
    });
}

std::string_view TrimWhitespace(std::string_view value);

bool LooksLikeOrphanClosureTailLine(std::string_view trimmed) {
    if (trimmed.empty() || StartsWith(trimmed, "--")) {
        return false;
    }

    if (trimmed == "end" || trimmed == "return" || StartsWith(trimmed, "return ")) {
        return true;
    }

    const std::string_view local_name = ExtractLocalDeclarationName(trimmed);
    if (!local_name.empty() && LooksLikeDecompilerTemporaryName(local_name) &&
        trimmed.find('=') != std::string_view::npos) {
        return true;
    }

    const std::string_view leading = ExtractLeadingIdentifier(trimmed);
    return !leading.empty() && LooksLikeDecompilerTemporaryName(leading) &&
           trimmed.find('=') != std::string_view::npos;
}

std::string_view TrimWhitespace(std::string_view value) {
    std::size_t begin = 0;
    while (begin < value.size() &&
           std::isspace(static_cast<unsigned char>(value[begin])) != 0) {
        ++begin;
    }

    std::size_t end = value.size();
    while (end > begin &&
           std::isspace(static_cast<unsigned char>(value[end - 1])) != 0) {
        --end;
    }

    return value.substr(begin, end - begin);
}

std::vector<std::string> SplitLines(std::string_view text, bool* had_trailing_newline) {
    std::vector<std::string> lines;
    std::size_t start = 0;
    while (start < text.size()) {
        const std::size_t end = text.find('\n', start);
        if (end == std::string_view::npos) {
            lines.emplace_back(text.substr(start));
            start = text.size();
        } else {
            lines.emplace_back(text.substr(start, end - start));
            start = end + 1;
        }
    }

    if (had_trailing_newline != nullptr) {
        *had_trailing_newline = !text.empty() && text.back() == '\n';
    }

    if (text.empty()) {
        lines.emplace_back();
    }

    return lines;
}

std::string JoinLines(const std::vector<std::string>& lines, bool had_trailing_newline) {
    std::string joined;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (i != 0) {
            joined.push_back('\n');
        }
        joined.append(lines[i]);
    }
    if (had_trailing_newline && !joined.empty()) {
        joined.push_back('\n');
    }
    return joined;
}

bool TryEvaluateExactParityCandidate(
    const refii::lua::audit::LuaBytecodeParityReference& parity_reference,
    std::string_view candidate_source,
    std::string_view chunk_name,
    refii::lua::audit::LuaBytecodeParityResult* parity_result);

bool ApplySharedRenderedLuaCleanupProfile(
    std::vector<std::string>* lines,
    refii::lua::internal::RenderedLuaCleanupProfile profile);

using RenderedLuaCleanupStepFn = bool (*)(std::vector<std::string>* lines);

bool TryApplyValidatedOutputCleanupProfile(std::vector<std::string>* lines) {
    return ApplySharedRenderedLuaCleanupProfile(
        lines, refii::lua::internal::RenderedLuaCleanupProfile::kValidatedOutput);
}

bool NormalizeRenderedLuaNearExactCreateSubClassSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    std::size_t first_function_line = lines->size();
    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed.starts_with("function ") || trimmed.starts_with("local function ") ||
            trimmed.find("= function(") != std::string_view::npos) {
            first_function_line = index;
            break;
        }
    }

    bool changed = false;
    for (std::size_t index = 0; index < lines->size(); ++index) {
        if ((*lines)[index].find("--") != std::string::npos) {
            continue;
        }

        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[index], &is_local, &lhs_names, &rhs) ||
            is_local || lhs_names.size() != 1) {
            continue;
        }

        const std::string lhs =
            std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
        if (!refii::lua::IsValidLuaIdentifier(lhs)) {
            continue;
        }

        const std::string trimmed_rhs =
            std::string(refii::lua::TrimLuaWhitespace(rhs));
        if (trimmed_rhs.size() <= 20 || !trimmed_rhs.ends_with(')')) {
            continue;
        }

        const std::size_t call_pos = trimmed_rhs.find(":CreateSubClass(");
        if (call_pos == std::string::npos || call_pos == 0) {
            continue;
        }
        const std::string receiver =
            std::string(refii::lua::TrimLuaWhitespace(trimmed_rhs.substr(0, call_pos)));
        if (receiver.empty() || receiver.find('[') != std::string::npos ||
            receiver.find(':') != std::string::npos) {
            continue;
        }
        bool receiver_valid = true;
        {
            std::size_t start = 0;
            while (start < receiver.size()) {
                const std::size_t dot = receiver.find('.', start);
                const std::size_t end =
                    dot == std::string::npos ? receiver.size() : dot;
                const std::string_view segment(receiver.data() + start, end - start);
                if (segment.empty() || !refii::lua::IsValidLuaIdentifier(segment)) {
                    receiver_valid = false;
                    break;
                }
                if (dot == std::string::npos) {
                    break;
                }
                start = dot + 1;
            }
        }
        if (!receiver_valid) {
            continue;
        }

        const std::size_t arg_start = call_pos + std::string(":CreateSubClass(").size();
        const std::string_view arg_view =
            refii::lua::TrimLuaWhitespace(std::string_view(trimmed_rhs).substr(
                arg_start, trimmed_rhs.size() - arg_start - 1));
        if (arg_view.size() < 2) {
            continue;
        }
        const char quote = arg_view.front();
        if ((quote != '"' && quote != '\'') || arg_view.back() != quote) {
            continue;
        }
        const std::string class_name(arg_view.substr(1, arg_view.size() - 2));
        if (class_name != lhs) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[index]);
        const std::string rewritten = indent + trimmed_rhs;
        if (rewritten == (*lines)[index]) {
            continue;
        }
        (*lines)[index] = rewritten;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaNearExactCreateSubClassSeedsFallback(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    std::size_t first_function_line = lines->size();
    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed.starts_with("function ") || trimmed.starts_with("local function ") ||
            trimmed.find("= function(") != std::string_view::npos) {
            first_function_line = index;
            break;
        }
    }

    bool changed = false;
    for (std::size_t index = 0; index < lines->size() && index <= first_function_line; ++index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed.empty()) {
            continue;
        }

        const std::size_t equals_pos = trimmed.find('=');
        const std::size_t call_pos = trimmed.find(":CreateSubClass(");
        if (equals_pos == std::string_view::npos || call_pos == std::string_view::npos ||
            equals_pos >= call_pos) {
            continue;
        }

        const std::string lhs = std::string(
            refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
        if (!refii::lua::IsValidLuaIdentifier(lhs)) {
            continue;
        }

        const std::string rhs = std::string(
            refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
        if (rhs.empty()) {
            continue;
        }

        const std::string call_suffix = ":CreateSubClass(\"" + lhs + "\")";
        if (!EndsWith(rhs, call_suffix)) {
            continue;
        }

        const std::string receiver = std::string(refii::lua::TrimLuaWhitespace(
            rhs.substr(0, rhs.size() - call_suffix.size())));
        if (receiver.empty() || receiver.find('[') != std::string::npos ||
            receiver.find(':') != std::string::npos) {
            continue;
        }
        bool receiver_valid = true;
        {
            std::size_t start = 0;
            while (start < receiver.size()) {
                const std::size_t dot = receiver.find('.', start);
                const std::size_t end =
                    dot == std::string::npos ? receiver.size() : dot;
                const std::string_view segment(receiver.data() + start, end - start);
                if (segment.empty() || !refii::lua::IsValidLuaIdentifier(segment)) {
                    receiver_valid = false;
                    break;
                }
                if (dot == std::string::npos) {
                    break;
                }
                start = dot + 1;
            }
        }
        if (!receiver_valid) {
            continue;
        }

        const std::string rewritten = GetLuaLineIndent((*lines)[index]) + rhs;
        if (rewritten == (*lines)[index]) {
            continue;
        }
        (*lines)[index] = rewritten;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaNearExactSingletonFieldTableAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_singleton_table_literal_entry =
        [](std::string_view rhs_literal,
           std::string* key_out,
           std::string* value_out,
           bool* key_is_identifier_out) -> bool {
            if (key_out == nullptr || value_out == nullptr || key_is_identifier_out == nullptr) {
                return false;
            }

            const std::string rhs_trimmed =
                std::string(refii::lua::TrimLuaWhitespace(rhs_literal));
            if (rhs_trimmed.size() < 5 || rhs_trimmed.front() != '{' ||
                rhs_trimmed.back() != '}') {
                return false;
            }

            const std::string_view body =
                refii::lua::TrimLuaWhitespace(std::string_view(rhs_trimmed).substr(
                    1, rhs_trimmed.size() - 2));
            if (body.empty()) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t equals_pos = std::string_view::npos;
            bool has_top_level_comma = false;
            for (std::size_t cursor = 0; cursor < body.size(); ++cursor) {
                const char ch = body[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    paren_depth = std::max(0, paren_depth - 1);
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    brace_depth = std::max(0, brace_depth - 1);
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    bracket_depth = std::max(0, bracket_depth - 1);
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                if (ch == ',') {
                    has_top_level_comma = true;
                    break;
                }
                if (ch == '=' && equals_pos == std::string::npos) {
                    equals_pos = cursor;
                }
            }

            if (has_top_level_comma || equals_pos == std::string::npos || equals_pos == 0 ||
                equals_pos + 1 >= body.size()) {
                return false;
            }

            const std::string key = std::string(
                refii::lua::TrimLuaWhitespace(body.substr(0, equals_pos)));
            const std::string value = std::string(
                refii::lua::TrimLuaWhitespace(body.substr(equals_pos + 1)));
            if (key.empty() || value.empty()) {
                return false;
            }

            bool key_is_identifier = false;
            if (refii::lua::IsValidLuaIdentifier(key)) {
                key_is_identifier = true;
            } else if (key.size() >= 3 && key.front() == '[' && key.back() == ']') {
                const std::string_view inner =
                    refii::lua::TrimLuaWhitespace(std::string_view(key).substr(1, key.size() - 2));
                if (inner.empty()) {
                    return false;
                }
            } else {
                return false;
            }

            *key_out = key;
            *value_out = value;
            *key_is_identifier_out = key_is_identifier;
            return true;
        };

    const auto is_likely_reference_carry_rhs = [](std::string_view rhs_expression) {
        const std::string_view rhs = refii::lua::TrimLuaWhitespace(rhs_expression);
        if (rhs.empty() || rhs == "{}" || rhs == "nil" || rhs == "true" || rhs == "false") {
            return false;
        }
        if (rhs.front() == '"' || rhs.front() == '\'' ||
            std::isdigit(static_cast<unsigned char>(rhs.front())) != 0 ||
            rhs.front() == '{' || rhs.front() == '(') {
            return false;
        }
        if (rhs.find(':') != std::string_view::npos ||
            rhs.find('(') != std::string_view::npos ||
            rhs.find(')') != std::string_view::npos ||
            rhs.find('{') != std::string_view::npos ||
            rhs.find('}') != std::string_view::npos ||
            rhs.find(',') != std::string_view::npos) {
            return false;
        }

        auto parse_identifier_end = [](std::string_view text, std::size_t start) {
            if (start >= text.size() ||
                !(std::isalpha(static_cast<unsigned char>(text[start])) != 0 ||
                  text[start] == '_')) {
                return start;
            }
            std::size_t end = start + 1;
            while (end < text.size() &&
                   (std::isalnum(static_cast<unsigned char>(text[end])) != 0 ||
                    text[end] == '_')) {
                ++end;
            }
            return end;
        };

        std::size_t cursor = parse_identifier_end(rhs, 0);
        if (cursor == 0) {
            return false;
        }
        while (cursor < rhs.size()) {
            if (rhs[cursor] == '.') {
                const std::size_t next = parse_identifier_end(rhs, cursor + 1);
                if (next == cursor + 1) {
                    return false;
                }
                cursor = next;
                continue;
            }
            if (rhs[cursor] == '[') {
                std::size_t close = cursor + 1;
                int depth = 1;
                bool in_single_quote = false;
                bool in_double_quote = false;
                while (close < rhs.size()) {
                    const char ch = rhs[close];
                    if (in_single_quote) {
                        if (ch == '\\' && close + 1 < rhs.size()) {
                            ++close;
                        } else if (ch == '\'') {
                            in_single_quote = false;
                        }
                        ++close;
                        continue;
                    }
                    if (in_double_quote) {
                        if (ch == '\\' && close + 1 < rhs.size()) {
                            ++close;
                        } else if (ch == '"') {
                            in_double_quote = false;
                        }
                        ++close;
                        continue;
                    }
                    if (ch == '\'') {
                        in_single_quote = true;
                        ++close;
                        continue;
                    }
                    if (ch == '"') {
                        in_double_quote = true;
                        ++close;
                        continue;
                    }
                    if (ch == '[') {
                        ++depth;
                    } else if (ch == ']') {
                        --depth;
                        if (depth == 0) {
                            break;
                        }
                    }
                    ++close;
                }
                if (close >= rhs.size() || depth != 0) {
                    return false;
                }
                const std::string_view inner =
                    refii::lua::TrimLuaWhitespace(rhs.substr(cursor + 1, close - cursor - 1));
                if (inner.empty()) {
                    return false;
                }
                cursor = close + 1;
                continue;
            }
            return false;
        }

        return true;
    };

    const auto has_prior_lhs_table_context =
        [&](std::size_t assignment_index, std::string_view lhs_expression) {
            if (assignment_index == 0 || lhs_expression.empty()) {
                return false;
            }

            const std::size_t lower_bound = 0;
            const std::string lhs_prefix = std::string(lhs_expression) + ".";
            const std::string lhs_index_prefix = std::string(lhs_expression) + "[";
            std::size_t assignment_cluster_hits = 0;

            for (std::size_t probe = assignment_index; probe-- > lower_bound;) {
                const std::string_view trimmed =
                    refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }

                if (trimmed.starts_with(lhs_prefix) || trimmed.starts_with(lhs_index_prefix) ||
                    trimmed.find(lhs_prefix) != std::string_view::npos ||
                    trimmed.find(lhs_index_prefix) != std::string_view::npos) {
                    return true;
                }

                bool probe_is_local = false;
                std::vector<std::string> probe_lhs_names;
                std::string probe_rhs;
                if (!refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                             &probe_is_local,
                                                             &probe_lhs_names,
                                                             &probe_rhs) ||
                    probe_lhs_names.size() != 1) {
                    if (assignment_index - probe > 24) {
                        break;
                    }
                    continue;
                }

                const std::string probe_lhs =
                    std::string(refii::lua::TrimLuaWhitespace(probe_lhs_names.front()));
                if (probe_lhs != lhs_expression) {
                    if (assignment_index - probe > 24) {
                        break;
                    }
                    continue;
                }

                const std::string probe_rhs_trimmed =
                    std::string(refii::lua::TrimLuaWhitespace(probe_rhs));
                if (probe_rhs_trimmed == "{}") {
                    return true;
                }
                if (assignment_index > probe &&
                    assignment_index - probe <= 2 &&
                    is_likely_reference_carry_rhs(probe_rhs_trimmed)) {
                    return true;
                }

                std::string parsed_key;
                std::string parsed_value;
                bool parsed_key_is_identifier = false;
                if (parse_singleton_table_literal_entry(probe_rhs_trimmed,
                                                        &parsed_key,
                                                        &parsed_value,
                                                        &parsed_key_is_identifier)) {
                    ++assignment_cluster_hits;
                    if (assignment_cluster_hits >= 2) {
                        return true;
                    }
                }

                if (assignment_index - probe > 24) {
                    break;
                }
            }

            return false;
        };

    const auto has_forward_lhs_singleton_cluster =
        [&](std::size_t assignment_index, std::string_view lhs_expression) {
            if (lhs_expression.empty() || assignment_index + 1 >= lines->size()) {
                return false;
            }

            std::size_t singleton_hits = 0;
            for (std::size_t probe = assignment_index + 1;
                 probe < lines->size() && probe <= assignment_index + 10;
                 ++probe) {
                const std::string_view trimmed =
                    refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }

                bool probe_is_local = false;
                std::vector<std::string> probe_lhs_names;
                std::string probe_rhs;
                std::string probe_lhs;
                if (!refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                             &probe_is_local,
                                                             &probe_lhs_names,
                                                             &probe_rhs) ||
                    probe_is_local || probe_lhs_names.size() != 1) {
                    const std::size_t eq = trimmed.find('=');
                    if (eq == std::string_view::npos || eq == 0 ||
                        eq + 1 >= trimmed.size() ||
                        trimmed.find("==") != std::string_view::npos) {
                        break;
                    }
                    probe_lhs = std::string(
                        refii::lua::TrimLuaWhitespace(trimmed.substr(0, eq)));
                    probe_rhs = std::string(
                        refii::lua::TrimLuaWhitespace(trimmed.substr(eq + 1)));
                } else {
                    probe_lhs = std::string(
                        refii::lua::TrimLuaWhitespace(probe_lhs_names.front()));
                }

                if (probe_lhs != lhs_expression) {
                    break;
                }

                std::string parsed_key;
                std::string parsed_value;
                bool parsed_key_is_identifier = false;
                if (!parse_singleton_table_literal_entry(probe_rhs,
                                                         &parsed_key,
                                                         &parsed_value,
                                                         &parsed_key_is_identifier)) {
                    break;
                }
                ++singleton_hits;
            }

            return singleton_hits >= 1;
        };

    std::size_t first_function_line = lines->size();
    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed.starts_with("function ") || trimmed.starts_with("local function ") ||
            trimmed.find("= function(") != std::string_view::npos) {
            first_function_line = index;
            break;
        }
    }

    bool changed = false;
    for (std::size_t index = 0; index < lines->size(); ++index) {
        if ((*lines)[index].find("--") != std::string::npos) {
            continue;
        }

        const std::string_view trimmed_line =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed_line.starts_with("local ")) {
            continue;
        }

        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment((*lines)[index], &indent, &lhs, &rhs)) {
            bool is_local = false;
            std::vector<std::string> lhs_names;
            if (!refii::lua::ParseRenderedAssignmentLine(
                    (*lines)[index], &is_local, &lhs_names, &rhs) ||
                is_local || lhs_names.size() != 1) {
                const std::string_view raw_trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[index]));
                const std::size_t eq = raw_trimmed.find('=');
                if (eq == std::string_view::npos || eq == 0 || eq + 1 >= raw_trimmed.size()) {
                    continue;
                }
                lhs = std::string(refii::lua::TrimLuaWhitespace(raw_trimmed.substr(0, eq)));
                rhs = std::string(refii::lua::TrimLuaWhitespace(raw_trimmed.substr(eq + 1)));
                if (lhs.empty() || rhs.empty() || raw_trimmed.find("==") != std::string_view::npos) {
                    continue;
                }
                indent = GetLuaLineIndent((*lines)[index]);
                goto parsed_assignment_line;
            }
            indent = GetLuaLineIndent((*lines)[index]);
            lhs = lhs_names.front();
        }
parsed_assignment_line:

        const std::string lhs_trimmed =
            std::string(refii::lua::TrimLuaWhitespace(lhs));
        if (lhs_trimmed.empty() ||
            lhs_trimmed.find(',') != std::string::npos ||
            (lhs_trimmed.find('[') == std::string::npos &&
             lhs_trimmed.find('.') == std::string::npos)) {
            continue;
        }
        const bool is_group_members_shape =
            lhs_trimmed.find(".GroupMembers[") != std::string::npos ||
            lhs_trimmed.find(".groupmembers[") != std::string::npos;
        if (index >= first_function_line && !is_group_members_shape) {
            const bool has_prior_context = has_prior_lhs_table_context(index, lhs_trimmed);
            if (!has_prior_context &&
                !has_forward_lhs_singleton_cluster(index, lhs_trimmed)) {
                continue;
            }
        }
        if (index >= first_function_line && is_group_members_shape) {
            std::size_t containing_start = lines->size();
            std::size_t containing_end = lines->size();
            for (std::size_t probe = index + 1; probe-- > first_function_line;) {
                if (!refii::lua::IsRenderedFunctionStartLine((*lines)[probe])) {
                    continue;
                }
                const std::size_t end =
                    refii::lua::FindRenderedFunctionBlockEnd(*lines, probe);
                if (end >= lines->size() || end < index) {
                    continue;
                }
                containing_start = probe;
                containing_end = end;
                break;
            }
            if (containing_start == lines->size() || containing_end == lines->size()) {
                continue;
            }
            // Keep function-body singleton table rewrites limited to short setter/getter
            // style spans; larger bodies have mixed ownership patterns and are safer to
            // leave on fallback.
            if (containing_end - containing_start > 20) {
                continue;
            }
        }

        std::string key;
        std::string value;
        bool key_is_identifier = false;
        if (!parse_singleton_table_literal_entry(rhs, &key, &value, &key_is_identifier)) {
            continue;
        }

        const std::string leading_indent = GetLuaLineIndent((*lines)[index]);
        const std::string rewritten = key_is_identifier
                                          ? (leading_indent + lhs_trimmed + "." + key + " = " + value)
                                          : (leading_indent + lhs_trimmed + key + " = " + value);
        if (rewritten == (*lines)[index]) {
            continue;
        }
        (*lines)[index] = rewritten;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaNearExactTopLevelSingletonFieldSeeds(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    std::size_t first_function_line = lines->size();
    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed.starts_with("function ") || trimmed.starts_with("local function ") ||
            trimmed.find("= function(") != std::string_view::npos) {
            first_function_line = index;
            break;
        }
    }

    bool changed = false;
    for (std::size_t index = 0; index < first_function_line; ++index) {
        if ((*lines)[index].find("--") != std::string::npos) {
            continue;
        }

        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[index],
                                                     &is_local,
                                                     &lhs_names,
                                                     &rhs) ||
            is_local || lhs_names.size() != 1) {
            continue;
        }

        const std::string lhs_trimmed =
            std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
        const std::string rhs_trimmed =
            std::string(refii::lua::TrimLuaWhitespace(rhs));
        if (!refii::lua::IsValidLuaIdentifier(lhs_trimmed) ||
            rhs_trimmed.size() < 5 ||
            rhs_trimmed.front() != '{' ||
            rhs_trimmed.back() != '}') {
            continue;
        }

        const std::string_view body = refii::lua::TrimLuaWhitespace(
            std::string_view(rhs_trimmed).substr(1, rhs_trimmed.size() - 2));
        if (body.empty()) {
            continue;
        }
        if (body.find(',') != std::string_view::npos) {
            continue;
        }

        const std::size_t equals_pos = body.find('=');
        if (equals_pos == std::string_view::npos || equals_pos + 1 >= body.size()) {
            continue;
        }

        const std::string key = std::string(
            refii::lua::TrimLuaWhitespace(body.substr(0, equals_pos)));
        const std::string value = std::string(
            refii::lua::TrimLuaWhitespace(body.substr(equals_pos + 1)));
        if (!refii::lua::IsValidLuaIdentifier(key) || value != "{}") {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[index]);
        const std::string rewritten_seed = indent + lhs_trimmed + " = {}";
        const std::string rewritten_field =
            indent + lhs_trimmed + "." + key + " = " + value;
        if ((*lines)[index] == rewritten_seed &&
            index + 1 < lines->size() &&
            (*lines)[index + 1] == rewritten_field) {
            continue;
        }

        (*lines)[index] = rewritten_seed;
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(index + 1),
                      rewritten_field);
        ++first_function_line;
        ++index;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaRedundantTableReseedsBeforeFieldWrites(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs_expression) ||
                lhs_names.size() != 1) {
                continue;
            }

            const std::string lhs =
                std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            const std::string rhs =
                std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
            if (lhs.empty() || refii::lua::IsRenderTemporaryName(lhs) || rhs != "{}") {
                continue;
            }

            bool seen_prior_context = false;
            const std::string lhs_field_prefix = lhs + ".";
            const std::string lhs_index_prefix = lhs + "[";
            for (std::size_t probe = line_index; probe-- > 0;) {
                const std::string_view trimmed =
                    refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }

                if (trimmed.starts_with(lhs_field_prefix) || trimmed.starts_with(lhs_index_prefix) ||
                    trimmed.find(lhs_field_prefix) != std::string_view::npos ||
                    trimmed.find(lhs_index_prefix) != std::string_view::npos) {
                    seen_prior_context = true;
                    break;
                }

                bool prior_is_local = false;
                std::vector<std::string> prior_lhs_names;
                std::string prior_rhs_expression;
                if (refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                            &prior_is_local,
                                                            &prior_lhs_names,
                                                            &prior_rhs_expression) &&
                    prior_lhs_names.size() == 1 &&
                    std::string(refii::lua::TrimLuaWhitespace(prior_lhs_names.front())) == lhs) {
                    seen_prior_context = true;
                    break;
                }

                std::string field_key;
                std::string field_rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (refii::lua::ParseRenderedTableFieldAssignment((*lines)[probe],
                                                                  lhs,
                                                                  &field_key,
                                                                  &field_rhs_expression,
                                                                  &rhs_is_function,
                                                                  &array_index)) {
                    seen_prior_context = true;
                    break;
                }
            }
            if (!seen_prior_context) {
                continue;
            }

            bool has_immediate_field_write = false;
            for (std::size_t probe = line_index + 1;
                 probe < lines->size() && probe < line_index + 8;
                 ++probe) {
                const std::string_view trimmed =
                    refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }

                if (trimmed.starts_with(lhs_field_prefix) ||
                    trimmed.starts_with(lhs_index_prefix)) {
                    has_immediate_field_write = true;
                    break;
                }

                bool probe_is_local = false;
                std::vector<std::string> probe_lhs_names;
                std::string probe_rhs_expression;
                if (refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                            &probe_is_local,
                                                            &probe_lhs_names,
                                                            &probe_rhs_expression) &&
                    probe_lhs_names.size() == 1 &&
                    std::string(refii::lua::TrimLuaWhitespace(probe_lhs_names.front())) == lhs) {
                    break;
                }

                std::string field_key;
                std::string field_rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (refii::lua::ParseRenderedTableFieldAssignment((*lines)[probe],
                                                                  lhs,
                                                                  &field_key,
                                                                  &field_rhs_expression,
                                                                  &rhs_is_function,
                                                                  &array_index)) {
                    has_immediate_field_write = true;
                }
                break;
            }

            if (!has_immediate_field_write) {
                continue;
            }

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaSeededSingletonTableReplacementChains(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto try_parse_nonlocal_single_assignment =
        [](const std::string& line, std::string* lhs_out, std::string* rhs_out) -> bool {
            if (lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *lhs_out = std::string();
            *rhs_out = std::string();

            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &is_local, &lhs_names, &rhs_expression) &&
                !is_local && lhs_names.size() == 1) {
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty() && !rhs_out->empty();
            }

            std::string indent;
            std::string lhs_expression;
            if (ParseSimpleRenderedAssignment(line, &indent, &lhs_expression, rhs_out)) {
                *lhs_out =
                    std::string(refii::lua::TrimLuaWhitespace(std::string_view(lhs_expression)));
                *rhs_out =
                    std::string(refii::lua::TrimLuaWhitespace(std::string_view(*rhs_out)));
                if (!lhs_out->empty() && !rhs_out->empty() && !lhs_out->starts_with("local ")) {
                    return true;
                }
            }

            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            const std::size_t eq_pos = trimmed.find('=');
            if (eq_pos == std::string_view::npos || eq_pos == 0 || eq_pos + 1 >= trimmed.size() ||
                trimmed.find("==") != std::string_view::npos) {
                return false;
            }
            const std::string lhs =
                std::string(refii::lua::TrimLuaWhitespace(trimmed.substr(0, eq_pos)));
            const std::string rhs =
                std::string(refii::lua::TrimLuaWhitespace(trimmed.substr(eq_pos + 1)));
            if (lhs.empty() || rhs.empty() || lhs.starts_with("local ")) {
                return false;
            }
            *lhs_out = lhs;
            *rhs_out = rhs;
            return true;
        };

    const auto split_top_level_entries =
        [](std::string_view body, std::vector<std::string>* entries_out) -> bool {
            if (entries_out == nullptr) {
                return false;
            }
            entries_out->clear();
            body = refii::lua::TrimLuaWhitespace(body);
            if (body.empty()) {
                return true;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t token_start = 0;
            for (std::size_t cursor = 0; cursor < body.size(); ++cursor) {
                const char ch = body[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    paren_depth = std::max(0, paren_depth - 1);
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    brace_depth = std::max(0, brace_depth - 1);
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    bracket_depth = std::max(0, bracket_depth - 1);
                    continue;
                }

                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                if (ch != ',') {
                    continue;
                }

                const std::string token = std::string(
                    refii::lua::TrimLuaWhitespace(body.substr(token_start, cursor - token_start)));
                if (!token.empty()) {
                    entries_out->push_back(token);
                }
                token_start = cursor + 1;
            }

            const std::string trailing = std::string(
                refii::lua::TrimLuaWhitespace(body.substr(token_start)));
            if (!trailing.empty()) {
                entries_out->push_back(trailing);
            }
            return true;
        };

    const auto parse_seeded_singleton_assignment =
        [&](std::string_view rhs_literal, std::string* suffix_out, std::string* value_out) -> bool {
            if (suffix_out == nullptr || value_out == nullptr) {
                return false;
            }
            *suffix_out = std::string();
            *value_out = std::string();

            const std::string rhs_trimmed =
                std::string(refii::lua::TrimLuaWhitespace(rhs_literal));
            if (rhs_trimmed.size() < 2 || rhs_trimmed.front() != '{' ||
                rhs_trimmed.back() != '}') {
                return false;
            }

            std::vector<std::string> entries;
            if (!split_top_level_entries(std::string_view(rhs_trimmed).substr(
                                             1, rhs_trimmed.size() - 2),
                                         &entries) ||
                entries.empty() || entries.size() > 2) {
                return false;
            }

            auto parse_key_value = [](std::string_view entry_text,
                                      std::string* key_out,
                                      std::string* value_out,
                                      bool* key_is_identifier_out) {
                if (key_out == nullptr || value_out == nullptr || key_is_identifier_out == nullptr) {
                    return false;
                }
                *key_out = std::string();
                *value_out = std::string();
                *key_is_identifier_out = false;

                bool in_single_quote = false;
                bool in_double_quote = false;
                int paren_depth = 0;
                int brace_depth = 0;
                int bracket_depth = 0;
                std::size_t equals_pos = std::string_view::npos;
                for (std::size_t cursor = 0; cursor < entry_text.size(); ++cursor) {
                    const char ch = entry_text[cursor];
                    if (in_single_quote) {
                        if (ch == '\\' && cursor + 1 < entry_text.size()) {
                            ++cursor;
                        } else if (ch == '\'') {
                            in_single_quote = false;
                        }
                        continue;
                    }
                    if (in_double_quote) {
                        if (ch == '\\' && cursor + 1 < entry_text.size()) {
                            ++cursor;
                        } else if (ch == '"') {
                            in_double_quote = false;
                        }
                        continue;
                    }
                    if (ch == '\'') {
                        in_single_quote = true;
                        continue;
                    }
                    if (ch == '"') {
                        in_double_quote = true;
                        continue;
                    }
                    if (ch == '(') {
                        ++paren_depth;
                        continue;
                    }
                    if (ch == ')') {
                        paren_depth = std::max(0, paren_depth - 1);
                        continue;
                    }
                    if (ch == '{') {
                        ++brace_depth;
                        continue;
                    }
                    if (ch == '}') {
                        brace_depth = std::max(0, brace_depth - 1);
                        continue;
                    }
                    if (ch == '[') {
                        ++bracket_depth;
                        continue;
                    }
                    if (ch == ']') {
                        bracket_depth = std::max(0, bracket_depth - 1);
                        continue;
                    }
                    if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                        continue;
                    }
                    if (ch == '=') {
                        equals_pos = cursor;
                        break;
                    }
                }
                if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                    equals_pos + 1 >= entry_text.size()) {
                    return false;
                }
                const std::string key = std::string(
                    refii::lua::TrimLuaWhitespace(entry_text.substr(0, equals_pos)));
                const std::string value = std::string(
                    refii::lua::TrimLuaWhitespace(entry_text.substr(equals_pos + 1)));
                if (key.empty() || value.empty()) {
                    return false;
                }

                bool key_is_identifier = false;
                if (refii::lua::IsValidLuaIdentifier(key)) {
                    key_is_identifier = true;
                } else if (key.size() >= 3 && key.front() == '[' && key.back() == ']') {
                    const std::string_view key_inner = refii::lua::TrimLuaWhitespace(
                        std::string_view(key).substr(1, key.size() - 2));
                    if (key_inner.empty()) {
                        return false;
                    }
                } else {
                    return false;
                }

                *key_out = key;
                *value_out = value;
                *key_is_identifier_out = key_is_identifier;
                return true;
            };

            if (entries.size() == 1) {
                std::string key;
                std::string value;
                bool key_is_identifier = false;
                if (parse_key_value(entries.front(), &key, &value, &key_is_identifier)) {
                    *suffix_out = key_is_identifier ? ("." + key) : key;
                    *value_out = value;
                    return true;
                }
                // Positional singleton: {expr} -> [1] = expr
                if (entries.front() != "{}") {
                    *suffix_out = "[1]";
                    *value_out = entries.front();
                    return true;
                }
                return false;
            }

            // Sparse two-entry seed forms, e.g. { {}, expr } -> [2] = expr
            if (entries[0] == "{}" && entries[1] != "{}") {
                *suffix_out = "[2]";
                *value_out = entries[1];
                return true;
            }
            if (entries[0] != "{}" && entries[1] == "{}") {
                *suffix_out = "[1]";
                *value_out = entries[0];
                return true;
            }

            return false;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string lhs;
        std::string seed_rhs_expression;
        if (!try_parse_nonlocal_single_assignment(
                (*lines)[line_index], &lhs, &seed_rhs_expression)) {
            continue;
        }
        const std::string seed_rhs =
            std::string(refii::lua::TrimLuaWhitespace(seed_rhs_expression));
        if (lhs.empty() || seed_rhs != "{}" ||
            (lhs.find('.') == std::string::npos && lhs.find('[') == std::string::npos)) {
            continue;
        }

        std::size_t rewrites = 0;
        for (std::size_t probe = line_index + 1;
             probe < lines->size() && probe <= line_index + 24;
             ++probe) {
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                break;
            }

            std::string probe_lhs;
            std::string probe_rhs_expression;
            if (!try_parse_nonlocal_single_assignment(
                    (*lines)[probe], &probe_lhs, &probe_rhs_expression)) {
                break;
            }
            if (probe_lhs != lhs) {
                break;
            }

            std::string rewritten_suffix;
            std::string rewritten_value;
            if (!parse_seeded_singleton_assignment(probe_rhs_expression,
                                                   &rewritten_suffix,
                                                   &rewritten_value)) {
                break;
            }

            const std::string rewritten_line =
                GetLuaLineIndent((*lines)[probe]) + lhs + rewritten_suffix + " = " +
                rewritten_value;
            if (rewritten_line != (*lines)[probe]) {
                (*lines)[probe] = rewritten_line;
                changed = true;
            }
            ++rewrites;
        }

        if (rewrites == 0) {
            continue;
        }

        // Drop trailing destructive reseed when it immediately follows an already
        // normalized singleton replacement chain.
        std::size_t trailing_probe = line_index + 1;
        while (trailing_probe < lines->size()) {
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[trailing_probe]));
            if (trimmed.empty()) {
                ++trailing_probe;
                continue;
            }

            std::string trailing_lhs;
            std::string trailing_rhs_expression;
            if (try_parse_nonlocal_single_assignment(
                    (*lines)[trailing_probe], &trailing_lhs, &trailing_rhs_expression) &&
                trailing_lhs == lhs &&
                std::string(refii::lua::TrimLuaWhitespace(trailing_rhs_expression)) == "{}") {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(trailing_probe));
                changed = true;
            }
            break;
        }
    }

    return changed;
}

bool NormalizeRenderedLuaHashedKeyLoadTableAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto try_parse_nonlocal_single_assignment =
        [](const std::string& line, std::string* lhs_out, std::string* rhs_out) -> bool {
            if (lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *lhs_out = std::string();
            *rhs_out = std::string();

            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &is_local, &lhs_names, &rhs_expression) &&
                !is_local && lhs_names.size() == 1) {
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty() && !rhs_out->empty();
            }

            std::string indent;
            std::string lhs_expression;
            if (ParseSimpleRenderedAssignment(line, &indent, &lhs_expression, rhs_out)) {
                *lhs_out =
                    std::string(refii::lua::TrimLuaWhitespace(std::string_view(lhs_expression)));
                *rhs_out =
                    std::string(refii::lua::TrimLuaWhitespace(std::string_view(*rhs_out)));
                if (!lhs_out->empty() && !rhs_out->empty() && !lhs_out->starts_with("local ")) {
                    return true;
                }
            }
            return false;
        };

    const auto extract_indexed_table_target =
        [](std::string_view lhs, std::string* table_out, std::string* index_out) -> bool {
            if (table_out == nullptr || index_out == nullptr) {
                return false;
            }
            *table_out = std::string();
            *index_out = std::string();
            lhs = refii::lua::TrimLuaWhitespace(lhs);
            if (lhs.empty() || lhs.back() != ']') {
                return false;
            }
            const std::size_t open_bracket = lhs.rfind('[');
            if (open_bracket == std::string_view::npos || open_bracket == 0 ||
                open_bracket + 1 >= lhs.size() - 1) {
                return false;
            }
            const std::string table =
                std::string(refii::lua::TrimLuaWhitespace(lhs.substr(0, open_bracket)));
            const std::string index =
                std::string(refii::lua::TrimLuaWhitespace(
                    lhs.substr(open_bracket + 1, lhs.size() - open_bracket - 2)));
            if (table.empty() || index.empty()) {
                return false;
            }
            *table_out = table;
            *index_out = index;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string lhs;
        std::string rhs;
        if (!try_parse_nonlocal_single_assignment((*lines)[line_index], &lhs, &rhs)) {
            continue;
        }
        if (!rhs.starts_with("FNVHash(") || !rhs.ends_with(")")) {
            continue;
        }

        std::string table_expr;
        std::string source_key_expr;
        if (!extract_indexed_table_target(lhs, &table_expr, &source_key_expr)) {
            continue;
        }

        std::size_t hashed_key_use_line = lines->size();
        for (std::size_t probe = line_index + 1;
             probe < lines->size() && probe <= line_index + 12;
             ++probe) {
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                break;
            }
            if (trimmed.find("local hashed_key =") != std::string_view::npos) {
                hashed_key_use_line = lines->size();
                break;
            }

            std::string probe_lhs;
            std::string probe_rhs;
            if (!try_parse_nonlocal_single_assignment(
                    (*lines)[probe], &probe_lhs, &probe_rhs)) {
                continue;
            }
            if (probe_rhs != source_key_expr || probe_lhs.find("[hashed_key]") == std::string::npos) {
                continue;
            }
            hashed_key_use_line = probe;
            break;
        }

        if (hashed_key_use_line == lines->size()) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] = indent + "local hashed_key = " + rhs;
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + table_expr + "[" + source_key_expr + "] = hashed_key");
        changed = true;
        ++line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaSparseSingletonTableReplacementChains(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto try_parse_nonlocal_single_assignment =
        [](const std::string& line, std::string* lhs_out, std::string* rhs_out) -> bool {
            if (lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *lhs_out = std::string();
            *rhs_out = std::string();

            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &is_local, &lhs_names, &rhs_expression) &&
                !is_local && lhs_names.size() == 1) {
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty() && !rhs_out->empty();
            }

            std::string indent;
            std::string lhs_expression;
            if (ParseSimpleRenderedAssignment(line, &indent, &lhs_expression, rhs_out)) {
                *lhs_out =
                    std::string(refii::lua::TrimLuaWhitespace(std::string_view(lhs_expression)));
                *rhs_out =
                    std::string(refii::lua::TrimLuaWhitespace(std::string_view(*rhs_out)));
                if (!lhs_out->empty() && !rhs_out->empty() && !lhs_out->starts_with("local ")) {
                    return true;
                }
            }
            return false;
        };

    const auto split_top_level_entries =
        [](std::string_view body, std::vector<std::string>* entries_out) -> bool {
            if (entries_out == nullptr) {
                return false;
            }
            entries_out->clear();
            body = refii::lua::TrimLuaWhitespace(body);
            if (body.empty()) {
                return true;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t token_start = 0;
            for (std::size_t cursor = 0; cursor < body.size(); ++cursor) {
                const char ch = body[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    paren_depth = std::max(0, paren_depth - 1);
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    brace_depth = std::max(0, brace_depth - 1);
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    bracket_depth = std::max(0, bracket_depth - 1);
                    continue;
                }

                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                if (ch != ',') {
                    continue;
                }

                const std::string token = std::string(
                    refii::lua::TrimLuaWhitespace(body.substr(token_start, cursor - token_start)));
                entries_out->push_back(token);
                token_start = cursor + 1;
            }

            entries_out->push_back(std::string(
                refii::lua::TrimLuaWhitespace(body.substr(token_start))));
            return true;
        };

    const auto collect_rhs_table_literal =
        [&](std::size_t start_line,
            std::string_view initial_rhs,
            std::string* literal_out,
            std::size_t* end_line_out) -> bool {
            if (literal_out == nullptr || end_line_out == nullptr) {
                return false;
            }
            literal_out->clear();
            *end_line_out = start_line;

            std::string rhs = std::string(refii::lua::TrimLuaWhitespace(initial_rhs));
            if (rhs.empty() || rhs.front() != '{') {
                return false;
            }
            if (rhs.back() == '}') {
                *literal_out = rhs;
                return true;
            }
            if (rhs != "{") {
                return false;
            }

            int brace_depth = 1;
            bool in_single_quote = false;
            bool in_double_quote = false;
            std::string literal = "{";
            const std::size_t kMaxScanLines = 48;
            for (std::size_t probe = start_line + 1;
                 probe < lines->size() && probe <= start_line + kMaxScanLines;
                 ++probe) {
                const std::string_view trimmed =
                    refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                literal += " ";
                literal += std::string(trimmed);

                for (std::size_t cursor = 0; cursor < trimmed.size(); ++cursor) {
                    const char ch = trimmed[cursor];
                    if (in_single_quote) {
                        if (ch == '\\' && cursor + 1 < trimmed.size()) {
                            ++cursor;
                        } else if (ch == '\'') {
                            in_single_quote = false;
                        }
                        continue;
                    }
                    if (in_double_quote) {
                        if (ch == '\\' && cursor + 1 < trimmed.size()) {
                            ++cursor;
                        } else if (ch == '"') {
                            in_double_quote = false;
                        }
                        continue;
                    }
                    if (ch == '\'') {
                        in_single_quote = true;
                        continue;
                    }
                    if (ch == '"') {
                        in_double_quote = true;
                        continue;
                    }
                    if (ch == '{') {
                        ++brace_depth;
                    } else if (ch == '}') {
                        --brace_depth;
                    }
                }

                if (brace_depth <= 0) {
                    *literal_out = literal;
                    *end_line_out = probe;
                    return true;
                }
            }

            return false;
        };

    const auto try_parse_sparse_singleton_literal =
        [&](std::string_view rhs_literal,
            std::string* index_out,
            std::string* value_out) -> bool {
            if (index_out == nullptr || value_out == nullptr) {
                return false;
            }
            *index_out = std::string();
            *value_out = std::string();

            const std::string rhs_trimmed =
                std::string(refii::lua::TrimLuaWhitespace(rhs_literal));
            if (rhs_trimmed.size() < 2 || rhs_trimmed.front() != '{' ||
                rhs_trimmed.back() != '}') {
                return false;
            }
            const std::string_view body =
                refii::lua::TrimLuaWhitespace(std::string_view(rhs_trimmed).substr(
                    1, rhs_trimmed.size() - 2));

            std::vector<std::string> entries;
            if (!split_top_level_entries(body, &entries) || entries.empty()) {
                return false;
            }

            std::size_t non_empty_index = std::string::npos;
            std::string non_empty_entry;
            for (std::size_t index = 0; index < entries.size(); ++index) {
                std::string entry =
                    std::string(refii::lua::TrimLuaWhitespace(entries[index]));
                if (!entry.empty() && entry.back() == ',') {
                    entry.pop_back();
                    entry = std::string(refii::lua::TrimLuaWhitespace(entry));
                }
                if (entry.empty()) {
                    continue;
                }
                if (entry == "{}") {
                    continue;
                }
                if (!non_empty_entry.empty()) {
                    return false;
                }
                non_empty_entry = entry;
                non_empty_index = index + 1;
            }
            if (non_empty_entry.empty() || non_empty_index == std::string::npos) {
                return false;
            }

            if (non_empty_entry.front() == '[') {
                const std::size_t close = non_empty_entry.find(']');
                const std::size_t eq = non_empty_entry.find('=', close + 1);
                if (close == std::string::npos || close <= 1 || eq == std::string::npos ||
                    eq + 1 >= non_empty_entry.size()) {
                    return false;
                }
                *index_out = std::string(refii::lua::TrimLuaWhitespace(
                    std::string_view(non_empty_entry).substr(1, close - 1)));
                *value_out = std::string(refii::lua::TrimLuaWhitespace(
                    std::string_view(non_empty_entry).substr(eq + 1)));
                return !index_out->empty() && !value_out->empty();
            }

            if (non_empty_entry.front() == '{' && non_empty_entry.back() == '}') {
                *index_out = std::to_string(non_empty_index);
                *value_out = non_empty_entry;
                return true;
            }

            if (non_empty_entry.find('=') != std::string::npos) {
                return false;
            }

            *index_out = std::to_string(non_empty_index);
            *value_out = non_empty_entry;
            return true;
        };

    struct SparseChainItem {
        std::size_t start_line = 0;
        std::size_t end_line = 0;
        std::string index_expression;
        std::string value_expression;
        std::string indent;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string lhs;
        std::string rhs_expression;
        if (!try_parse_nonlocal_single_assignment(
                (*lines)[line_index], &lhs, &rhs_expression)) {
            continue;
        }
        if (lhs.empty() ||
            (lhs.find('.') == std::string::npos && lhs.find('[') == std::string::npos)) {
            continue;
        }

        std::string rhs_literal;
        std::size_t block_end_line = line_index;
        if (!collect_rhs_table_literal(line_index,
                                       rhs_expression,
                                       &rhs_literal,
                                       &block_end_line)) {
            continue;
        }

        std::string index_expression;
        std::string value_expression;
        if (!try_parse_sparse_singleton_literal(rhs_literal,
                                                &index_expression,
                                                &value_expression)) {
            continue;
        }

        std::vector<SparseChainItem> chain;
        chain.push_back({line_index,
                         block_end_line,
                         index_expression,
                         value_expression,
                         GetLuaLineIndent((*lines)[line_index])});

        std::size_t probe = block_end_line + 1;
        std::size_t trailing_reset_start = lines->size();
        std::size_t trailing_reset_end = lines->size();
        while (probe < lines->size()) {
            while (probe < lines->size() &&
                   refii::lua::TrimLuaWhitespace(
                       refii::lua::StripLuaLineComment((*lines)[probe]))
                       .empty()) {
                ++probe;
            }
            if (probe >= lines->size()) {
                break;
            }
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
            if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                break;
            }

            std::string probe_lhs;
            std::string probe_rhs_expression;
            if (!try_parse_nonlocal_single_assignment(
                    (*lines)[probe], &probe_lhs, &probe_rhs_expression) ||
                probe_lhs != lhs) {
                break;
            }

            std::string probe_rhs_literal;
            std::size_t probe_end_line = probe;
            if (!collect_rhs_table_literal(probe,
                                           probe_rhs_expression,
                                           &probe_rhs_literal,
                                           &probe_end_line)) {
                break;
            }
            if (std::string(refii::lua::TrimLuaWhitespace(probe_rhs_literal)) == "{}") {
                trailing_reset_start = probe;
                trailing_reset_end = probe_end_line;
                break;
            }

            std::string probe_index_expression;
            std::string probe_value_expression;
            if (!try_parse_sparse_singleton_literal(probe_rhs_literal,
                                                    &probe_index_expression,
                                                    &probe_value_expression)) {
                break;
            }
            chain.push_back({probe,
                             probe_end_line,
                             probe_index_expression,
                             probe_value_expression,
                             GetLuaLineIndent((*lines)[probe])});
            probe = probe_end_line + 1;
        }

        if (chain.size() < 2 || chain.front().end_line <= chain.front().start_line) {
            continue;
        }

        const SparseChainItem& first = chain.front();
        (*lines)[first.start_line] = first.indent + lhs + " = {}";
        (*lines)[first.start_line + 1] =
            first.indent + lhs + "[" + first.index_expression + "] = " +
            first.value_expression;
        for (std::size_t clear_line = first.start_line + 2;
             clear_line <= first.end_line;
             ++clear_line) {
            (*lines)[clear_line].clear();
        }

        for (std::size_t item_index = 1; item_index < chain.size(); ++item_index) {
            const SparseChainItem& item = chain[item_index];
            (*lines)[item.start_line] =
                item.indent + lhs + "[" + item.index_expression + "] = " +
                item.value_expression;
            for (std::size_t clear_line = item.start_line + 1;
                 clear_line <= item.end_line;
                 ++clear_line) {
                (*lines)[clear_line].clear();
            }
        }

        if (trailing_reset_start < lines->size()) {
            for (std::size_t clear_line = trailing_reset_start;
                 clear_line <= trailing_reset_end && clear_line < lines->size();
                 ++clear_line) {
                (*lines)[clear_line].clear();
            }
        }

        changed = true;
        line_index = chain.back().end_line;
    }

    return changed;
}

bool NormalizeRenderedLuaChainedTableElementAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto split_top_level_commas =
        [](std::string_view text, std::vector<std::string>* tokens_out) -> bool {
            if (tokens_out == nullptr) {
                return false;
            }
            tokens_out->clear();

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t token_start = 0;
            for (std::size_t cursor = 0; cursor < text.size(); ++cursor) {
                const char ch = text[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < text.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < text.size()) {
                        ++cursor;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    paren_depth = std::max(0, paren_depth - 1);
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    brace_depth = std::max(0, brace_depth - 1);
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    bracket_depth = std::max(0, bracket_depth - 1);
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                if (ch != ',') {
                    continue;
                }

                tokens_out->push_back(std::string(
                    refii::lua::TrimLuaWhitespace(text.substr(token_start, cursor - token_start))));
                token_start = cursor + 1;
            }

            tokens_out->push_back(std::string(
                refii::lua::TrimLuaWhitespace(text.substr(token_start))));
            return true;
        };

    const auto parse_base_index_from_lhs =
        [](std::string_view lhs, std::string* base_out, std::string* index_out) -> bool {
            if (base_out == nullptr || index_out == nullptr) {
                return false;
            }
            *base_out = std::string();
            *index_out = std::string();

            lhs = refii::lua::TrimLuaWhitespace(lhs);
            if (lhs.empty() || lhs.back() != ']') {
                return false;
            }

            std::size_t open_bracket = std::string_view::npos;
            int depth = 0;
            for (std::size_t cursor = lhs.size(); cursor-- > 0;) {
                const char ch = lhs[cursor];
                if (ch == ']') {
                    ++depth;
                } else if (ch == '[') {
                    --depth;
                    if (depth == 0) {
                        open_bracket = cursor;
                        break;
                    }
                }
            }
            if (open_bracket == std::string_view::npos || open_bracket == 0 ||
                open_bracket + 1 >= lhs.size() - 1) {
                return false;
            }

            const std::string base = std::string(
                refii::lua::TrimLuaWhitespace(lhs.substr(0, open_bracket)));
            const std::string index = std::string(
                refii::lua::TrimLuaWhitespace(lhs.substr(
                    open_bracket + 1, lhs.size() - open_bracket - 2)));
            if (base.empty() || index.empty()) {
                return false;
            }

            *base_out = base;
            *index_out = index;
            return true;
        };

    const auto parse_bracket_key_value_token =
        [](std::string_view token, std::string* index_out, std::string* value_out) -> bool {
            if (index_out == nullptr || value_out == nullptr) {
                return false;
            }
            *index_out = std::string();
            *value_out = std::string();

            token = refii::lua::TrimLuaWhitespace(token);
            if (token.empty() || token.front() != '[') {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t close_bracket = std::string_view::npos;
            for (std::size_t cursor = 0; cursor < token.size(); ++cursor) {
                const char ch = token[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < token.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < token.size()) {
                        ++cursor;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    paren_depth = std::max(0, paren_depth - 1);
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    brace_depth = std::max(0, brace_depth - 1);
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    --bracket_depth;
                    if (bracket_depth == 0) {
                        close_bracket = cursor;
                        break;
                    }
                    continue;
                }
            }

            if (close_bracket == std::string_view::npos || close_bracket <= 1 ||
                close_bracket + 1 >= token.size()) {
                return false;
            }

            const std::size_t equals_pos = token.find('=', close_bracket + 1);
            if (equals_pos == std::string_view::npos || equals_pos + 1 >= token.size()) {
                return false;
            }

            const std::string index = std::string(
                refii::lua::TrimLuaWhitespace(token.substr(1, close_bracket - 1)));
            const std::string value = std::string(
                refii::lua::TrimLuaWhitespace(token.substr(equals_pos + 1)));
            if (index.empty() || value.empty()) {
                return false;
            }

            *index_out = index;
            *value_out = value;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment((*lines)[line_index], &indent, &lhs, &rhs)) {
            continue;
        }
        if (lhs.find('[') == std::string::npos || rhs.find(',') == std::string::npos) {
            continue;
        }

        std::string base;
        std::string first_index;
        if (!parse_base_index_from_lhs(lhs, &base, &first_index)) {
            continue;
        }

        std::vector<std::string> rhs_tokens;
        if (!split_top_level_commas(rhs, &rhs_tokens) || rhs_tokens.size() <= 1) {
            continue;
        }
        const std::string first_value =
            std::string(refii::lua::TrimLuaWhitespace(rhs_tokens.front()));
        if (first_value.empty()) {
            continue;
        }

        std::vector<std::pair<std::string, std::string>> assignments;
        assignments.emplace_back(first_index, first_value);
        bool all_following_valid = true;
        for (std::size_t token_index = 1; token_index < rhs_tokens.size(); ++token_index) {
            std::string idx;
            std::string value;
            if (!parse_bracket_key_value_token(rhs_tokens[token_index], &idx, &value)) {
                all_following_valid = false;
                break;
            }
            assignments.emplace_back(std::move(idx), std::move(value));
        }
        if (!all_following_valid || assignments.size() <= 1) {
            continue;
        }

        (*lines)[line_index] = indent + base + "[" + assignments.front().first + "] = " +
                               assignments.front().second;
        std::size_t insertion_index = line_index + 1;
        for (std::size_t assign_index = 1; assign_index < assignments.size(); ++assign_index) {
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(insertion_index),
                          indent + base + "[" + assignments[assign_index].first + "] = " +
                              assignments[assign_index].second);
            ++insertion_index;
        }
        line_index = insertion_index - 1;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaSameTargetSingletonLiteralOverwrites(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto is_safe_target = [](std::string_view expression) {
        expression = refii::lua::TrimLuaWhitespace(expression);
        if (expression.empty() || expression.find('(') != std::string_view::npos ||
            expression.find(')') != std::string_view::npos ||
            expression.find(',') != std::string_view::npos) {
            return false;
        }
        std::size_t token_start = 0;
        while (token_start < expression.size()) {
            const std::size_t dot = expression.find('.', token_start);
            const std::string_view token =
                dot == std::string_view::npos
                    ? expression.substr(token_start)
                    : expression.substr(token_start, dot - token_start);
            if (!refii::lua::IsValidLuaIdentifier(
                    std::string(refii::lua::TrimLuaWhitespace(token)))) {
                return false;
            }
            if (dot == std::string_view::npos) {
                break;
            }
            token_start = dot + 1;
        }
        return true;
    };

    const auto parse_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs_expression) ||
                lhs_names.size() != 1) {
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment(line));
                const std::size_t equals_pos = trimmed.find('=');
                if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                    equals_pos + 1 >= trimmed.size() ||
                    trimmed.find("==") != std::string_view::npos ||
                    trimmed.find("~=") != std::string_view::npos ||
                    trimmed.find("<=") != std::string_view::npos ||
                    trimmed.find(">=") != std::string_view::npos) {
                    return false;
                }
                const std::string lhs = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
                const std::string rhs = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
                if (lhs.empty() || rhs.empty()) {
                    return false;
                }
                *is_local_out = lhs.starts_with("local ");
                *lhs_out = *is_local_out
                               ? std::string(refii::lua::TrimLuaWhitespace(
                                     std::string_view(lhs).substr(6)))
                               : lhs;
                *rhs_out = rhs;
                return !lhs_out->empty();
            }
            *is_local_out = is_local;
            *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto parse_singleton_literal =
        [](std::string_view rhs, std::string* suffix_out, std::string* value_out) {
            if (suffix_out == nullptr || value_out == nullptr) {
                return false;
            }
            *suffix_out = std::string();
            *value_out = std::string();

            const std::string rhs_trimmed =
                std::string(refii::lua::TrimLuaWhitespace(rhs));
            if (rhs_trimmed.size() < 5 || rhs_trimmed.front() != '{' ||
                rhs_trimmed.back() != '}') {
                return false;
            }
            const std::string_view body = refii::lua::TrimLuaWhitespace(
                std::string_view(rhs_trimmed).substr(1, rhs_trimmed.size() - 2));
            if (body.empty()) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t equals_pos = std::string_view::npos;
            for (std::size_t cursor = 0; cursor < body.size(); ++cursor) {
                const char ch = body[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    paren_depth = std::max(0, paren_depth - 1);
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    brace_depth = std::max(0, brace_depth - 1);
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    bracket_depth = std::max(0, bracket_depth - 1);
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                if (ch == ',') {
                    return false;
                }
                if (ch == '=' && equals_pos == std::string_view::npos) {
                    equals_pos = cursor;
                }
            }
            if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                equals_pos + 1 >= body.size()) {
                return false;
            }

            const std::string key =
                std::string(refii::lua::TrimLuaWhitespace(body.substr(0, equals_pos)));
            const std::string value =
                std::string(refii::lua::TrimLuaWhitespace(body.substr(equals_pos + 1)));
            if (key.empty() || value.empty()) {
                return false;
            }

            if (refii::lua::IsValidLuaIdentifier(key)) {
                *suffix_out = "." + key;
            } else if (key.size() >= 3 && key.front() == '[' && key.back() == ']') {
                const std::string_view inner = refii::lua::TrimLuaWhitespace(
                    std::string_view(key).substr(1, key.size() - 2));
                if (inner.empty()) {
                    return false;
                }
                *suffix_out = key;
            } else {
                return false;
            }
            *value_out = value;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        if (!parse_assignment((*lines)[line_index],
                              &first_is_local,
                              &first_lhs,
                              &first_rhs) ||
            first_is_local || !is_safe_target(first_lhs)) {
            continue;
        }

        const std::string first_rhs_trimmed =
            std::string(refii::lua::TrimLuaWhitespace(first_rhs));
        if (first_rhs_trimmed.empty() || first_rhs_trimmed.front() == '{' ||
            first_rhs_trimmed.find("function") != std::string::npos) {
            continue;
        }

        std::size_t second_line = line_index + 1;
        while (second_line < lines->size() &&
               refii::lua::TrimLuaWhitespace(
                   refii::lua::StripLuaLineComment((*lines)[second_line]))
                   .empty()) {
            ++second_line;
        }
        if (second_line >= lines->size()) {
            continue;
        }

        if (GetLuaLineIndent((*lines)[second_line]) != GetLuaLineIndent((*lines)[line_index])) {
            continue;
        }

        bool second_is_local = false;
        std::string second_lhs;
        std::string second_rhs;
        if (!parse_assignment((*lines)[second_line],
                              &second_is_local,
                              &second_lhs,
                              &second_rhs) ||
            second_is_local || second_lhs != first_lhs) {
            continue;
        }

        std::string suffix;
        std::string value;
        if (!parse_singleton_literal(second_rhs, &suffix, &value)) {
            continue;
        }

        (*lines)[second_line] =
            GetLuaLineIndent((*lines)[second_line]) + first_lhs + suffix + " = " + value;
        changed = true;
    }
    return changed;
}

bool NormalizeRenderedLuaEmptyTableThenSingletonIndexAssignmentToLiteral(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                        &is_local,
                                                        &lhs_names,
                                                        &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty() && !rhs_out->empty();
            }

            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment(line));
            const std::size_t equals_pos = trimmed.find('=');
            if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                equals_pos + 1 >= trimmed.size() ||
                trimmed.find("==") != std::string_view::npos ||
                trimmed.find("~=") != std::string_view::npos ||
                trimmed.find("<=") != std::string_view::npos ||
                trimmed.find(">=") != std::string_view::npos) {
                return false;
            }
            std::string lhs = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
            const std::string rhs = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
            if (lhs.empty() || rhs.empty()) {
                return false;
            }
            *is_local_out = lhs.starts_with("local ");
            if (*is_local_out) {
                lhs = std::string(refii::lua::TrimLuaWhitespace(
                    std::string_view(lhs).substr(6)));
            }
            *lhs_out = lhs;
            *rhs_out = rhs;
            return !lhs_out->empty();
        };

    const auto is_simple_safe_target = [](std::string_view expression) {
        expression = refii::lua::TrimLuaWhitespace(expression);
        if (expression.empty() || expression.find('(') != std::string_view::npos ||
            expression.find(')') != std::string_view::npos ||
            expression.find(',') != std::string_view::npos ||
            expression.find('[') != std::string_view::npos ||
            expression.find(']') != std::string_view::npos) {
            return false;
        }
        std::size_t token_start = 0;
        while (token_start < expression.size()) {
            const std::size_t dot = expression.find('.', token_start);
            const std::string_view token =
                dot == std::string_view::npos
                    ? expression.substr(token_start)
                    : expression.substr(token_start, dot - token_start);
            if (!refii::lua::IsValidLuaIdentifier(
                    std::string(refii::lua::TrimLuaWhitespace(token)))) {
                return false;
            }
            if (dot == std::string_view::npos) {
                break;
            }
            token_start = dot + 1;
        }
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!parse_assignment((*lines)[line_index],
                              &seed_is_local,
                              &seed_lhs,
                              &seed_rhs) ||
            seed_rhs != "{}" || !is_simple_safe_target(seed_lhs)) {
            continue;
        }

        std::size_t patch_line = line_index + 1;
        while (patch_line < lines->size() &&
               refii::lua::TrimLuaWhitespace(
                   refii::lua::StripLuaLineComment((*lines)[patch_line]))
                   .empty()) {
            ++patch_line;
        }
        if (patch_line >= lines->size()) {
            continue;
        }

        if (GetLuaLineIndent((*lines)[patch_line]) != GetLuaLineIndent((*lines)[line_index])) {
            continue;
        }

        bool patch_is_local = false;
        std::string patch_lhs;
        std::string patch_rhs;
        if (!parse_assignment((*lines)[patch_line],
                              &patch_is_local,
                              &patch_lhs,
                              &patch_rhs) ||
            patch_is_local || patch_rhs.empty()) {
            continue;
        }

        const std::string prefix = seed_lhs + "[";
        if (!patch_lhs.starts_with(prefix) || patch_lhs.back() != ']') {
            continue;
        }
        const std::string key_expression =
            patch_lhs.substr(seed_lhs.size() + 1,
                             patch_lhs.size() - seed_lhs.size() - 2);
        if (refii::lua::TrimLuaWhitespace(key_expression).empty()) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] =
            indent + (seed_is_local ? "local " : "") + seed_lhs + " = {[" +
            key_expression + "] = " + patch_rhs + "}";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(patch_line));
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaDuplicateGroupMethodCallChains(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_identifier_end = [](std::string_view text, std::size_t start) {
        std::size_t end = start;
        while (end < text.size() &&
               (std::isalnum(static_cast<unsigned char>(text[end])) || text[end] == '_')) {
            ++end;
        }
        return end;
    };

    bool changed = false;
    for (std::string& line : *lines) {
        if (line.find(".Group:") == std::string::npos) {
            continue;
        }
        std::size_t cursor = 0;
        bool line_changed = false;
        while (cursor < line.size()) {
            const std::size_t first_colon = line.find(':', cursor);
            if (first_colon == std::string::npos || first_colon + 1 >= line.size()) {
                break;
            }
            const std::size_t first_method_end = parse_identifier_end(line, first_colon + 1);
            if (first_method_end <= first_colon + 1 ||
                first_method_end + 2 > line.size() ||
                line.compare(first_method_end, 2, "()") != 0) {
                cursor = first_colon + 1;
                continue;
            }

            const std::size_t group_pos = first_method_end + 2;
            if (group_pos + 7 >= line.size() ||
                line.compare(group_pos, 7, ".Group:") != 0) {
                cursor = first_colon + 1;
                continue;
            }

            const std::size_t second_method_start = group_pos + 7;
            const std::size_t second_method_end =
                parse_identifier_end(line, second_method_start);
            if (second_method_end <= second_method_start ||
                second_method_end + 2 > line.size() ||
                line.compare(second_method_end, 2, "()") != 0) {
                cursor = first_colon + 1;
                continue;
            }

            const std::string first_method =
                line.substr(first_colon + 1, first_method_end - first_colon - 1);
            const std::string second_method =
                line.substr(second_method_start, second_method_end - second_method_start);
            if (first_method != second_method) {
                cursor = first_colon + 1;
                continue;
            }

            line.replace(first_colon,
                         second_method_end + 2 - first_colon,
                         ":" + first_method + "()");
            line_changed = true;
            cursor = first_colon + first_method.size() + 3;
        }
        changed |= line_changed;
    }
    return changed;
}

bool NormalizeRenderedLuaRedundantExistingTableReseedsBeforeFieldWrite(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs_expression) ||
                lhs_names.size() != 1) {
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment(line));
                const std::size_t equals_pos = trimmed.find('=');
                if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                    equals_pos + 1 >= trimmed.size() ||
                    trimmed.find("==") != std::string_view::npos ||
                    trimmed.find("~=") != std::string_view::npos ||
                    trimmed.find("<=") != std::string_view::npos ||
                    trimmed.find(">=") != std::string_view::npos) {
                    return false;
                }
                std::string lhs = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
                const std::string rhs = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
                if (lhs.empty() || rhs.empty()) {
                    return false;
                }
                *is_local_out = lhs.starts_with("local ");
                if (*is_local_out) {
                    lhs = std::string(refii::lua::TrimLuaWhitespace(
                        std::string_view(lhs).substr(6)));
                }
                *lhs_out = lhs;
                *rhs_out = rhs;
                return !lhs_out->empty();
            }
            *is_local_out = is_local;
            *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto is_safe_dotted_target = [](std::string_view expression) {
        expression = refii::lua::TrimLuaWhitespace(expression);
        if (expression.empty() || expression.find('(') != std::string_view::npos ||
            expression.find(')') != std::string_view::npos ||
            expression.find(',') != std::string_view::npos ||
            expression.find('[') != std::string_view::npos ||
            expression.find(']') != std::string_view::npos) {
            return false;
        }
        std::size_t token_start = 0;
        while (token_start < expression.size()) {
            const std::size_t dot = expression.find('.', token_start);
            const std::string_view token =
                dot == std::string_view::npos
                    ? expression.substr(token_start)
                    : expression.substr(token_start, dot - token_start);
            if (!refii::lua::IsValidLuaIdentifier(
                    std::string(refii::lua::TrimLuaWhitespace(token)))) {
                return false;
            }
            if (dot == std::string_view::npos) {
                break;
            }
            token_start = dot + 1;
        }
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 1; line_index + 1 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!parse_assignment((*lines)[line_index],
                              &seed_is_local,
                              &seed_lhs,
                              &seed_rhs) ||
            seed_is_local || seed_rhs != "{}" || !is_safe_dotted_target(seed_lhs)) {
            continue;
        }

        std::size_t patch_line = line_index + 1;
        while (patch_line < lines->size() &&
               refii::lua::TrimLuaWhitespace(
                   refii::lua::StripLuaLineComment((*lines)[patch_line]))
                   .empty()) {
            ++patch_line;
        }
        if (patch_line >= lines->size() ||
            GetLuaLineIndent((*lines)[patch_line]) != GetLuaLineIndent((*lines)[line_index])) {
            continue;
        }

        bool patch_is_local = false;
        std::string patch_lhs;
        std::string patch_rhs;
        if (!parse_assignment((*lines)[patch_line],
                              &patch_is_local,
                              &patch_lhs,
                              &patch_rhs) ||
            patch_is_local || !patch_lhs.starts_with(seed_lhs + ".")) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaImmediateLocalTableLiteralCallPlaceholder(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_local_literal =
        [](std::string_view line, std::string* name_out, std::string* literal_out) {
            if (name_out == nullptr || literal_out == nullptr) {
                return false;
            }
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            if (!trimmed.starts_with("local ")) {
                return false;
            }
            const std::size_t equals_pos = trimmed.find('=');
            if (equals_pos == std::string_view::npos || equals_pos <= 6 ||
                equals_pos + 1 >= trimmed.size()) {
                return false;
            }
            const std::string name = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(6, equals_pos - 6)));
            const std::string literal = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
            if (!refii::lua::IsValidLuaIdentifier(name) ||
                literal.size() < 5 || literal.front() != '{' ||
                literal.back() != '}' || literal == "{}") {
                return false;
            }
            *name_out = name;
            *literal_out = literal;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        std::string local_name;
        std::string table_literal;
        if (!parse_local_literal((*lines)[line_index], &local_name, &table_literal)) {
            continue;
        }

        std::size_t call_line = line_index + 1;
        while (call_line < lines->size() &&
               refii::lua::TrimLuaWhitespace(
                   refii::lua::StripLuaLineComment((*lines)[call_line]))
                   .empty()) {
            ++call_line;
        }
        if (call_line >= lines->size() ||
            GetLuaLineIndent((*lines)[call_line]) != GetLuaLineIndent((*lines)[line_index])) {
            continue;
        }

        std::string& call_text = (*lines)[call_line];
        const std::string_view call_trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(call_text));
        if (call_trimmed.find('(') == std::string_view::npos ||
            call_trimmed.find("{}") == std::string_view::npos ||
            call_trimmed.find(local_name) != std::string_view::npos) {
            continue;
        }

        bool local_used_later = false;
        for (std::size_t probe = call_line + 1; probe < lines->size() && probe <= call_line + 8;
             ++probe) {
            const std::string_view probe_trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe_trimmed.starts_with("local " + local_name) ||
                probe_trimmed.starts_with(local_name + " =")) {
                break;
            }
            if (probe_trimmed.find(local_name) != std::string_view::npos) {
                local_used_later = true;
                break;
            }
        }
        if (local_used_later) {
            continue;
        }

        const std::size_t placeholder_pos = call_text.find("{}");
        if (placeholder_pos == std::string::npos) {
            continue;
        }
        call_text.replace(placeholder_pos, 2, table_literal);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaSeededGuardedCallArgumentScaffolds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs_expression) ||
                lhs_names.size() != 1) {
                return false;
            }
            *is_local_out = is_local;
            *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!parse_single_assignment((*lines)[line_index],
                                     &seed_is_local,
                                     &seed_lhs,
                                     &seed_rhs) ||
            !seed_is_local ||
            !seed_lhs.starts_with("refii_seeded_call_arg_") ||
            seed_rhs.find('(') == std::string::npos ||
            refii::lua::CountIdentifierUses(seed_rhs, seed_lhs) != 0) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string_view guard_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 1]));
        if (GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            guard_trimmed != "if not " + seed_lhs + " then") {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!parse_single_assignment((*lines)[line_index + 2],
                                     &fallback_is_local,
                                     &fallback_lhs,
                                     &fallback_rhs) ||
            fallback_is_local || fallback_lhs != seed_lhs || fallback_rhs.empty() ||
            refii::lua::CountIdentifierUses(fallback_rhs, seed_lhs) != 0) {
            continue;
        }

        const std::string_view end_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 3]));
        if (GetLuaLineIndent((*lines)[line_index + 3]) != indent || end_trimmed != "end") {
            continue;
        }

        std::string call_line = (*lines)[line_index + 4];
        if (GetLuaLineIndent(call_line) != indent ||
            refii::lua::CountIdentifierUses(call_line, seed_lhs) != 1 ||
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(call_line))
                    .find('(') == std::string_view::npos) {
            continue;
        }

        bool used_later = false;
        for (std::size_t probe = line_index + 5; probe < lines->size(); ++probe) {
            const std::string_view probe_trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent((*lines)[probe]) == indent &&
                (refii::lua::IsRenderedFunctionStartLine(probe_trimmed) ||
                 probe_trimmed == "end")) {
                break;
            }
            if (refii::lua::CountIdentifierUses((*lines)[probe], seed_lhs) != 0) {
                used_later = true;
                break;
            }
        }
        if (used_later) {
            continue;
        }

        const std::size_t use_pos = call_line.find(seed_lhs);
        if (use_pos == std::string::npos) {
            continue;
        }
        call_line.replace(use_pos, seed_lhs.size(), fallback_rhs);

        (*lines)[line_index] = indent + "if not " + seed_rhs + " then";
        (*lines)[line_index + 1] = indent + "  " +
                                   std::string(refii::lua::TrimLuaWhitespace(
                                       refii::lua::StripLuaLineComment(call_line)));
        (*lines)[line_index + 2] = indent + "end";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaAdjacentCopiedLocalCallSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::vector<std::string> first_lhs_names;
        std::string first_rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                     &first_is_local,
                                                     &first_lhs_names,
                                                     &first_rhs) ||
            !first_is_local || first_lhs_names.size() != 1 ||
            first_rhs.find('(') == std::string::npos ||
            refii::lua::CountIdentifierUses(first_rhs, first_lhs_names.front()) != 0) {
            continue;
        }

        bool second_is_local = false;
        std::vector<std::string> second_lhs_names;
        std::string second_rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index + 1],
                                                     &second_is_local,
                                                     &second_lhs_names,
                                                     &second_rhs) ||
            !second_is_local || second_lhs_names.size() != 1 ||
            second_rhs != first_lhs_names.front() ||
            GetLuaLineIndent((*lines)[line_index]) !=
                GetLuaLineIndent((*lines)[line_index + 1])) {
            continue;
        }

        std::string rewritten = (*lines)[line_index + 1];
        const std::size_t rhs_pos = rewritten.rfind(second_rhs);
        if (rhs_pos == std::string::npos) {
            continue;
        }
        rewritten.replace(rhs_pos, second_rhs.size(), first_rhs);
        (*lines)[line_index + 1] = std::move(rewritten);
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaRedundantRepeatedCallReassignments(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::vector<std::string> seed_lhs_names;
        std::string seed_rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                     &seed_is_local,
                                                     &seed_lhs_names,
                                                     &seed_rhs) ||
            !seed_is_local || seed_lhs_names.size() != 1 ||
            seed_rhs.find('(') == std::string::npos ||
            refii::lua::CountIdentifierUses(seed_rhs, seed_lhs_names.front()) != 0) {
            continue;
        }

        const std::string seed_lhs =
            std::string(refii::lua::TrimLuaWhitespace(seed_lhs_names.front()));
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        std::size_t reassignment_line = std::string::npos;
        for (std::size_t probe = line_index + 1;
             probe < lines->size() && probe <= line_index + 4;
             ++probe) {
            const std::string_view probe_trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent((*lines)[probe]) != indent ||
                refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                break;
            }

            bool probe_is_local = false;
            std::vector<std::string> probe_lhs_names;
            std::string probe_rhs;
            if (!refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                         &probe_is_local,
                                                         &probe_lhs_names,
                                                         &probe_rhs) ||
                probe_lhs_names.size() != 1) {
                break;
            }

            const std::string probe_lhs =
                std::string(refii::lua::TrimLuaWhitespace(probe_lhs_names.front()));
            if (!probe_is_local && probe_lhs == seed_lhs && probe_rhs == seed_rhs) {
                reassignment_line = probe;
                break;
            }
            if (refii::lua::CountIdentifierUses((*lines)[probe], seed_lhs) != 0 ||
                refii::lua::CountIdentifierUses((*lines)[probe], seed_rhs) != 0) {
                break;
            }
        }

        if (reassignment_line == std::string::npos) {
            continue;
        }
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(reassignment_line));
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaFailureGuardBooleanReturns(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto split_top_level_or =
        [](std::string_view expression, std::vector<std::string>* parts) {
            if (parts == nullptr) {
                return false;
            }
            parts->clear();
            int depth = 0;
            std::size_t part_start = 0;
            for (std::size_t i = 0; i + 4 <= expression.size(); ++i) {
                const char ch = expression[i];
                if (ch == '(') {
                    ++depth;
                } else if (ch == ')' && depth > 0) {
                    --depth;
                }
                if (depth == 0 && i + 4 <= expression.size() &&
                    expression.compare(i, 4, " or ") == 0) {
                    parts->push_back(std::string(refii::lua::TrimLuaWhitespace(
                        expression.substr(part_start, i - part_start))));
                    part_start = i + 4;
                    i += 3;
                }
            }
            parts->push_back(std::string(refii::lua::TrimLuaWhitespace(
                expression.substr(part_start))));
            return !parts->empty();
        };

    const auto find_top_level_operator =
        [](std::string_view expression, std::string_view op, std::size_t* pos_out) {
            if (pos_out == nullptr) {
                return false;
            }
            int depth = 0;
            for (std::size_t i = 0; i + op.size() <= expression.size(); ++i) {
                const char ch = expression[i];
                if (ch == '(') {
                    ++depth;
                } else if (ch == ')' && depth > 0) {
                    --depth;
                }
                if (depth == 0 && expression.compare(i, op.size(), op) == 0) {
                    *pos_out = i;
                    return true;
                }
            }
            return false;
        };

    const auto invert_failure_part =
        [&](std::string_view part, std::string* success_out) {
            if (success_out == nullptr) {
                return false;
            }
            part = refii::lua::TrimLuaWhitespace(part);
            std::size_t op_pos = std::string::npos;
            if (find_top_level_operator(part, ">=", &op_pos)) {
                const std::string lhs =
                    std::string(refii::lua::TrimLuaWhitespace(part.substr(0, op_pos)));
                const std::string rhs = std::string(
                    refii::lua::TrimLuaWhitespace(part.substr(op_pos + 2)));
                if (lhs.empty() || rhs.empty()) {
                    return false;
                }
                *success_out = lhs + " < " + rhs;
                return true;
            }
            if (find_top_level_operator(part, "<=", &op_pos)) {
                const std::string lhs =
                    std::string(refii::lua::TrimLuaWhitespace(part.substr(0, op_pos)));
                const std::string rhs = std::string(
                    refii::lua::TrimLuaWhitespace(part.substr(op_pos + 2)));
                if (lhs.empty() || rhs.empty()) {
                    return false;
                }
                *success_out = rhs + " < " + lhs;
                return true;
            }
            return false;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string_view if_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then") ||
            GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 3]) != indent) {
            continue;
        }

        const std::string_view first_return = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 1]));
        const std::string_view guard_end = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 2]));
        const std::string_view final_return = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 3]));
        if (first_return != "return false" || guard_end != "end" ||
            final_return != "return true") {
            continue;
        }

        const std::string condition = std::string(refii::lua::TrimLuaWhitespace(
            if_trimmed.substr(3, if_trimmed.size() - 3 - 5)));
        std::vector<std::string> failure_parts;
        if (!split_top_level_or(condition, &failure_parts) || failure_parts.empty()) {
            continue;
        }

        std::vector<std::string> success_parts;
        bool all_inverted = true;
        for (const std::string& failure_part : failure_parts) {
            std::string success_part;
            if (!invert_failure_part(failure_part, &success_part)) {
                all_inverted = false;
                break;
            }
            success_parts.push_back(success_part);
        }
        if (!all_inverted || success_parts.empty()) {
            continue;
        }

        std::string success_expression;
        for (const std::string& success_part : success_parts) {
            if (!success_expression.empty()) {
                success_expression += " and ";
            }
            success_expression += success_part;
        }
        (*lines)[line_index] = indent + "return " + success_expression;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaCallPredicateNotToFalseCompare(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string indent = GetLuaLineIndent(line);
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
        const bool is_if = trimmed.starts_with("if not ") && trimmed.ends_with(" then");
        const bool is_while = trimmed.starts_with("while not ") && trimmed.ends_with(" do");
        if (!is_if && !is_while) {
            continue;
        }

        const std::size_t prefix_size = is_if ? 7 : 10;
        const std::size_t suffix_size = is_if ? 5 : 3;
        const std::string expression = std::string(refii::lua::TrimLuaWhitespace(
            trimmed.substr(prefix_size, trimmed.size() - prefix_size - suffix_size)));
        if (expression.empty() || expression.find('(') == std::string::npos ||
            expression.find(" and ") != std::string::npos ||
            expression.find(" or ") != std::string::npos ||
            expression.starts_with("not ") || expression.find("==") != std::string::npos ||
            expression.find("~=") != std::string::npos ||
            expression.find("<=") != std::string::npos ||
            expression.find(">=") != std::string::npos) {
            continue;
        }

        line = indent + (is_if ? "if " : "while ") + expression +
               " == false" + (is_if ? " then" : " do");
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaSimpleCallFalseComparesToNot(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string indent = GetLuaLineIndent(line);
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
        const bool is_if = trimmed.starts_with("if ") && trimmed.ends_with(" then");
        const bool is_while = trimmed.starts_with("while ") && trimmed.ends_with(" do");
        if (!is_if && !is_while) {
            continue;
        }

        const std::size_t prefix_size = is_if ? 3 : 6;
        const std::size_t suffix_size = is_if ? 5 : 3;
        const std::string condition = std::string(refii::lua::TrimLuaWhitespace(
            trimmed.substr(prefix_size, trimmed.size() - prefix_size - suffix_size)));
        constexpr std::string_view kFalseSuffix = " == false";
        if (!condition.ends_with(kFalseSuffix)) {
            continue;
        }

        const std::string expression = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(0, condition.size() - kFalseSuffix.size())));
        if (expression.empty() || expression.starts_with("not ") ||
            expression.find('(') == std::string::npos ||
            expression.find(" and ") != std::string::npos ||
            expression.find(" or ") != std::string::npos ||
            expression.find("==") != std::string::npos ||
            expression.find("~=") != std::string::npos ||
            expression.find("<=") != std::string::npos ||
            expression.find(">=") != std::string::npos ||
            expression.find('<') != std::string::npos ||
            expression.find('>') != std::string::npos) {
            continue;
        }

        line = indent + (is_if ? "if not " : "while not ") + expression +
               (is_if ? " then" : " do");
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaFirstSimpleCallFalseCompareToNotForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    for (std::string& line : *lines) {
        const std::string indent = GetLuaLineIndent(line);
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
        const bool is_if = trimmed.starts_with("if ") && trimmed.ends_with(" then");
        const bool is_while = trimmed.starts_with("while ") && trimmed.ends_with(" do");
        if (!is_if && !is_while) {
            continue;
        }

        const std::size_t prefix_size = is_if ? 3 : 6;
        const std::size_t suffix_size = is_if ? 5 : 3;
        const std::string condition = std::string(refii::lua::TrimLuaWhitespace(
            trimmed.substr(prefix_size, trimmed.size() - prefix_size - suffix_size)));
        constexpr std::string_view kFalseSuffix = " == false";
        if (!condition.ends_with(kFalseSuffix)) {
            continue;
        }

        const std::string expression = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(0, condition.size() - kFalseSuffix.size())));
        if (expression.empty() || expression.starts_with("not ") ||
            expression.find('(') == std::string::npos ||
            expression.find(" and ") != std::string::npos ||
            expression.find(" or ") != std::string::npos ||
            expression.find("==") != std::string::npos ||
            expression.find("~=") != std::string::npos ||
            expression.find("<=") != std::string::npos ||
            expression.find(">=") != std::string::npos ||
            expression.find('<') != std::string::npos ||
            expression.find('>') != std::string::npos) {
            continue;
        }

        line = indent + (is_if ? "if not " : "while not ") + expression +
               (is_if ? " then" : " do");
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaFirstAndPredicateElseToNestedForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 6) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };

    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        const std::string_view header = trim_line((*lines)[line_index]);
        if (!header.starts_with("if ") || !header.ends_with(" then")) {
            continue;
        }
        const std::string condition = std::string(refii::lua::TrimLuaWhitespace(
            header.substr(3, header.size() - 8)));
        const std::size_t and_pos = condition.find(" and ");
        if (and_pos == std::string::npos ||
            condition.find(" and ", and_pos + 5) != std::string::npos ||
            condition.find(" or ") != std::string::npos) {
            continue;
        }
        const std::string lhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(0, and_pos)));
        const std::string rhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(and_pos + 5)));
        if (lhs.empty() || rhs.empty() || lhs.starts_with("not ") ||
            rhs.starts_with("not ") || lhs.find('(') == std::string::npos ||
            rhs.find('(') == std::string::npos) {
            continue;
        }

        std::size_t else_line = std::string::npos;
        std::size_t end_line = std::string::npos;
        int depth = 0;
        for (std::size_t probe = line_index;
             probe < std::min(lines->size(), line_index + 64);
             ++probe) {
            const std::string_view probe_trimmed = trim_line((*lines)[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe > line_index && depth == 1 && probe_trimmed == "else") {
                else_line = probe;
            }
            depth += refii::lua::GetRenderedLuaBlockDelta(probe_trimmed);
            if (probe > line_index && depth == 0) {
                end_line = probe;
                break;
            }
            if (depth < 0) {
                break;
            }
        }
        if (else_line == std::string::npos || end_line == std::string::npos ||
            else_line <= line_index + 1 || end_line <= else_line + 1) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] = indent + "if " + lhs + " then";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + "if " + rhs + " then");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(else_line + 1),
                      indent + "end");
        return true;
    }

    return false;
}

bool ParseSimpleRenderedCall(std::string_view expression,
                             std::string* callee_out,
                             std::vector<std::string>* arguments_out);

bool NormalizeRenderedLuaFirstImmediateVectorLocalCallArgumentForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 2) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };

    for (std::size_t line_index = 1; line_index < lines->size(); ++line_index) {
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        if (GetLuaLineIndent((*lines)[line_index - 1]) != indent) {
            continue;
        }

        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index - 1],
                                                     &is_local,
                                                     &lhs_names,
                                                     &rhs_expression) ||
            !is_local || lhs_names.size() != 1 ||
            !refii::lua::IsValidLuaIdentifier(lhs_names.front())) {
            continue;
        }
        rhs_expression = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
        if (!rhs_expression.starts_with("CVector3(")) {
            continue;
        }

        const std::string_view if_trimmed = trim_line((*lines)[line_index]);
        if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }
        const std::string call_expression = std::string(refii::lua::TrimLuaWhitespace(
            if_trimmed.substr(3, if_trimmed.size() - 8)));
        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseSimpleRenderedCall(call_expression, &callee, &arguments) ||
            arguments.size() < 2 || arguments.front().find("CVector3(") != 0) {
            continue;
        }
        if (callee != "self:MoveToPosition" &&
            callee != "self:MoveToPositionWithDirection") {
            continue;
        }

        arguments.front() = lhs_names.front();
        std::string joined_arguments;
        for (std::size_t argument_index = 0; argument_index < arguments.size();
             ++argument_index) {
            if (argument_index != 0) {
                joined_arguments += ", ";
            }
            joined_arguments += arguments[argument_index];
        }
        (*lines)[line_index] =
            indent + "if " + callee + "(" + joined_arguments + ") then";
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaFirstMoveToPositionLoopVectorParityShape(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 8) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto strip_outer_parens = [](std::string value) {
        value = std::string(refii::lua::TrimLuaWhitespace(value));
        if (value.size() >= 2 && value.front() == '(' && value.back() == ')') {
            int depth = 0;
            bool wraps_whole = true;
            for (std::size_t index = 0; index < value.size(); ++index) {
                const char ch = value[index];
                if (ch == '(') {
                    ++depth;
                } else if (ch == ')') {
                    --depth;
                    if (depth == 0 && index + 1 < value.size()) {
                        wraps_whole = false;
                        break;
                    }
                }
                if (depth < 0) {
                    wraps_whole = false;
                    break;
                }
            }
            if (wraps_whole && depth == 0) {
                value = std::string(refii::lua::TrimLuaWhitespace(
                    std::string_view(value).substr(1, value.size() - 2)));
            }
        }
        return value;
    };
    const auto parse_single_assignment =
        [](const std::string& line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
        std::vector<std::string> lhs_names;
        std::string rhs;
        bool is_local = false;
        if (!refii::lua::ParseRenderedAssignmentLine(
                line, &is_local, &lhs_names, &rhs) ||
            lhs_names.size() != 1 ||
            !refii::lua::IsValidLuaIdentifier(lhs_names.front())) {
            return false;
        }
        if (is_local_out != nullptr) {
            *is_local_out = is_local;
        }
        if (lhs_out != nullptr) {
            *lhs_out = lhs_names.front();
        }
        if (rhs_out != nullptr) {
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs));
        }
        return true;
    };
    const auto parse_scale_assignment =
        [&](const std::string& line,
            const std::string& expected_target,
            std::string* scale_out) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!parse_single_assignment(line, &is_local, &lhs, &rhs) ||
            is_local || lhs != expected_target) {
            return false;
        }
        rhs = strip_outer_parens(rhs);
        const std::string needle = " * " + expected_target;
        if (!rhs.ends_with(needle)) {
            return false;
        }
        std::string scale = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(rhs).substr(0, rhs.size() - needle.size())));
        if (scale.empty() || scale.find(expected_target) != std::string::npos) {
            return false;
        }
        if (scale_out != nullptr) {
            *scale_out = std::move(scale);
        }
        return true;
    };

    bool folded_scaled_vector_locals = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size();
         ++line_index) {
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        if (GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 3]) != indent) {
            continue;
        }

        bool first_is_local = false;
        bool second_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        std::string second_lhs;
        std::string second_rhs;
        if (!parse_single_assignment((*lines)[line_index],
                                     &first_is_local,
                                     &first_lhs,
                                     &first_rhs) ||
            !parse_single_assignment((*lines)[line_index + 2],
                                     &second_is_local,
                                     &second_lhs,
                                     &second_rhs) ||
            !first_is_local || !second_is_local || first_lhs == second_lhs) {
            continue;
        }

        std::string first_scale;
        std::string second_scale;
        if (!parse_scale_assignment((*lines)[line_index + 1],
                                    first_lhs,
                                    &first_scale) ||
            !parse_scale_assignment((*lines)[line_index + 3],
                                    second_lhs,
                                    &second_scale) ||
            first_scale != second_scale) {
            continue;
        }

        (*lines)[line_index] =
            indent + "local " + first_lhs + " = (" + first_scale + " * " +
            "(" + strip_outer_parens(first_rhs) + "))";
        (*lines)[line_index + 2] =
            indent + "local " + second_lhs + " = (" + second_scale + " * " +
            "(" + strip_outer_parens(second_rhs) + "))";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        folded_scaled_vector_locals = true;
        break;
    }

    if (!folded_scaled_vector_locals) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view if_trimmed = trim_line((*lines)[line_index]);
        if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then") ||
            if_trimmed.find("self:MoveToPosition(") == std::string_view::npos) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        int depth = 0;
        std::size_t end_line = std::string::npos;
        bool has_top_level_else = false;
        for (std::size_t probe = line_index; probe < lines->size(); ++probe) {
            const std::string_view probe_trimmed = trim_line((*lines)[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe > line_index && depth == 1 && probe_trimmed == "else") {
                has_top_level_else = true;
                break;
            }
            depth += refii::lua::GetRenderedLuaBlockDelta(probe_trimmed);
            if (probe > line_index && depth == 0) {
                end_line = probe;
                break;
            }
            if (depth < 0) {
                break;
            }
        }
        if (has_top_level_else || end_line == std::string::npos ||
            end_line + 1 >= lines->size() || trim_line((*lines)[end_line]) != "end" ||
            trim_line((*lines)[end_line + 1]) != "return" ||
            GetLuaLineIndent((*lines)[end_line]) != indent ||
            GetLuaLineIndent((*lines)[end_line + 1]) != indent) {
            continue;
        }

        bool inlined_turn_vector = false;
        for (std::size_t probe = line_index + 1; probe + 2 < end_line; ++probe) {
            const std::string probe_indent = GetLuaLineIndent((*lines)[probe]);
            if (GetLuaLineIndent((*lines)[probe + 1]) != probe_indent ||
                GetLuaLineIndent((*lines)[probe + 2]) != probe_indent) {
                continue;
            }

            bool first_is_local = false;
            bool second_is_local = false;
            std::string target;
            std::string call_rhs;
            std::string second_lhs;
            std::string subtract_rhs;
            if (!parse_single_assignment((*lines)[probe],
                                         &first_is_local,
                                         &target,
                                         &call_rhs) ||
                !parse_single_assignment((*lines)[probe + 1],
                                         &second_is_local,
                                         &second_lhs,
                                         &subtract_rhs) ||
                first_is_local || second_is_local || target != second_lhs ||
                call_rhs.find(":GetPosition()") == std::string::npos) {
                continue;
            }

            subtract_rhs = strip_outer_parens(subtract_rhs);
            const std::string rhs_suffix = " - " + target;
            if (!subtract_rhs.ends_with(rhs_suffix)) {
                continue;
            }
            const std::string base_expression =
                std::string(refii::lua::TrimLuaWhitespace(
                    std::string_view(subtract_rhs).substr(
                        0, subtract_rhs.size() - rhs_suffix.size())));
            if (base_expression.empty()) {
                continue;
            }

            const std::string_view turn_trimmed = trim_line((*lines)[probe + 2]);
            if (turn_trimmed.find(":TurnToFaceDirection(" + target + ")") ==
                std::string_view::npos) {
                continue;
            }

            (*lines)[probe] = probe_indent + "local " + target + " = (" +
                              base_expression + " - " + call_rhs + ")";
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(probe + 1));
            --end_line;
            inlined_turn_vector = true;
            break;
        }
        if (!inlined_turn_vector) {
            continue;
        }

        (*lines)[end_line] = indent + "else";
        (*lines)[end_line + 1] = indent + "return";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(end_line + 2),
                      indent + "end");
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaMoveToPositionFailureElseBlocks(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 4) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 1; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view print_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (print_trimmed !=
            "print(\"--- TOLD I COULD, BUT REALLY I CAN'T NAV ---\")") {
            continue;
        }
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string_view previous_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index - 1]));
        const std::string_view return_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 1]));
        const std::string_view outer_else_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 2]));
        if (previous_trimmed != "end" || return_trimmed != "return" ||
            outer_else_trimmed != "else" ||
            GetLuaLineIndent((*lines)[line_index - 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent) {
            continue;
        }

        bool has_nearby_move_to_position_guard = false;
        for (std::size_t probe = line_index; probe > 0 && probe + 12 > line_index; --probe) {
            const std::size_t index = probe - 1;
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[index]));
            if (probe_trimmed.starts_with("if ") &&
                probe_trimmed.find("self:MoveToPosition(") != std::string_view::npos &&
                probe_trimmed.ends_with(" then")) {
                has_nearby_move_to_position_guard = true;
                break;
            }
            if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                break;
            }
        }
        if (!has_nearby_move_to_position_guard) {
            continue;
        }

        (*lines)[line_index - 1] = indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2),
                      indent + "end");
        changed = true;
        line_index += 2;
    }

    return changed;
}

bool NormalizeRenderedLuaNavigationAndMoveGuardNesting(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then") ||
            trimmed.find("Navigation.CanNavigateToPosition(") == std::string_view::npos ||
            trimmed.find(" and ") == std::string_view::npos ||
            trimmed.find("self:MoveToPosition(") == std::string_view::npos) {
            continue;
        }

        const std::string condition = std::string(refii::lua::TrimLuaWhitespace(
            trimmed.substr(3, trimmed.size() - 3 - 5)));
        const std::size_t and_pos = condition.find(") and (");
        if (and_pos == std::string::npos || condition.front() != '(' ||
            condition.back() != ')') {
            continue;
        }

        const std::string nav_expr = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(1, and_pos - 1)));
        const std::string move_expr = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(and_pos + 7,
                                               condition.size() - (and_pos + 7) - 1)));
        if (nav_expr.find("Navigation.CanNavigateToPosition(") == std::string::npos ||
            move_expr.find("self:MoveToPosition(") == std::string::npos) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        std::size_t else_line = std::string::npos;
        int nested_depth = 0;
        for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (GetLuaLineIndent((*lines)[probe]) == indent && nested_depth == 0 &&
                probe_trimmed == "else") {
                else_line = probe;
                break;
            }
            if (probe_trimmed.starts_with("if ") && probe_trimmed.ends_with(" then")) {
                ++nested_depth;
            } else if (probe_trimmed == "end" && nested_depth > 0) {
                --nested_depth;
            }
            if (GetLuaLineIndent((*lines)[probe]) == indent && nested_depth == 0 &&
                probe_trimmed == "end") {
                break;
            }
        }
        if (else_line == std::string::npos || else_line + 1 >= lines->size()) {
            continue;
        }
        const std::string_view else_body = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[else_line + 1]));
        if (else_body != "print(\"--- CAN'T NAV ---\")") {
            continue;
        }

        (*lines)[line_index] = indent + "if " + nav_expr + " then";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + "if " + move_expr + " then");
        ++else_line;
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(else_line),
                      indent + "end");
        changed = true;
        line_index = else_line + 1;
    }

    return changed;
}

bool NormalizeRenderedLuaWhileTrueNestedNotBreakToOrGuard(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 7) {
        return false;
    }

    const auto parse_not_guard =
        [](std::string_view line, std::string* expression_out) {
            if (expression_out == nullptr) {
                return false;
            }
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            if (!trimmed.starts_with("if not ") || !trimmed.ends_with(" then")) {
                return false;
            }
            const std::string expression = std::string(refii::lua::TrimLuaWhitespace(
                trimmed.substr(7, trimmed.size() - 7 - 5)));
            if (expression.empty() || expression.find(" and ") != std::string::npos ||
                expression.find(" or ") != std::string::npos) {
                return false;
            }
            *expression_out = expression;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        const std::string_view while_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (while_trimmed != "while true do") {
            continue;
        }
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        for (std::size_t offset = 1; offset <= 5; ++offset) {
            if (GetLuaLineIndent((*lines)[line_index + offset]) != indent) {
                goto next_candidate;
            }
        }

        {
            std::string lhs;
            std::string rhs;
            if (!parse_not_guard((*lines)[line_index + 1], &lhs) ||
                !parse_not_guard((*lines)[line_index + 2], &rhs)) {
                goto next_candidate;
            }
            const std::string_view break_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[line_index + 3]));
            const std::string_view first_end = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[line_index + 4]));
            const std::string_view second_end = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[line_index + 5]));
            if (break_trimmed != "break" || first_end != "end" || second_end != "end") {
                goto next_candidate;
            }

            (*lines)[line_index] = indent + "while " + lhs + " or " + rhs + " do";
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                         lines->begin() + static_cast<std::ptrdiff_t>(line_index + 6));
            changed = true;
        }

    next_candidate:
        continue;
    }

    return changed;
}

bool NormalizeRenderedLuaEmptyNotGuardBeforeReturnToOrReturn(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 3) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view if_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (!if_trimmed.starts_with("if not ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string_view end_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 1]));
        const std::string_view return_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 2]));
        if (GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent ||
            end_trimmed != "end" || !return_trimmed.starts_with("return ")) {
            continue;
        }

        std::string first_expression = std::string(refii::lua::TrimLuaWhitespace(
            if_trimmed.substr(7, if_trimmed.size() - 7 - 5)));
        std::string second_expression =
            std::string(refii::lua::TrimLuaWhitespace(return_trimmed.substr(7)));
        if (first_expression.empty() || second_expression.empty() ||
            first_expression.find(" or ") != std::string::npos ||
            first_expression.find(" and ") != std::string::npos ||
            second_expression.find(',') != std::string::npos) {
            continue;
        }

        (*lines)[line_index] =
            indent + "return " + first_expression + " or " + second_expression;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
    }

    return changed;
}

bool RestoreRenderedLuaEmptyEnumTextElseIfAssignmentsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 3) {
        return false;
    }

    const auto parse_enum_suffix = [](std::string_view line,
                                      std::string* suffix_out) -> bool {
        if (suffix_out == nullptr) {
            return false;
        }
        suffix_out->clear();
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
        if (!trimmed.starts_with("elseif ") || !trimmed.ends_with(" then")) {
            return false;
        }
        const std::string_view condition =
            refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
        const std::size_t equals = condition.find(" == ");
        if (equals == std::string_view::npos) {
            return false;
        }
        const std::string_view rhs =
            refii::lua::TrimLuaWhitespace(condition.substr(equals + 4));
        const std::size_t dot = rhs.rfind('.');
        if (dot == std::string_view::npos || dot + 1 >= rhs.size()) {
            return false;
        }
        std::string suffix = std::string(rhs.substr(dot + 1));
        if (suffix.starts_with("EMS_")) {
            suffix.erase(0, 4);
        }
        if (suffix.empty()) {
            return false;
        }
        for (char ch : suffix) {
            if ((ch < 'A' || ch > 'Z') && ch != '_' && (ch < '0' || ch > '9')) {
                return false;
            }
        }
        *suffix_out = std::move(suffix);
        return true;
    };

    const auto parse_text_assignment =
        [](std::string_view line,
           std::string* lhs_out,
           std::string* text_out) -> bool {
            if (lhs_out == nullptr || text_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs_expression) ||
                is_local || lhs_names.size() != 1) {
                return false;
            }
            const std::string lhs =
                std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            const std::string rhs =
                std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
            if (!refii::lua::IsValidLuaIdentifier(lhs) || rhs.size() < 3 ||
                rhs.front() != '"' || rhs.back() != '"') {
                return false;
            }
            const std::string text = rhs.substr(1, rhs.size() - 2);
            if (!text.starts_with("TEXT_")) {
                return false;
            }
            const std::size_t last_underscore = text.rfind('_');
            if (last_underscore == std::string::npos || last_underscore + 1 >= text.size()) {
                return false;
            }
            *lhs_out = lhs;
            *text_out = text;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        std::string enum_suffix;
        if (!parse_enum_suffix((*lines)[line_index], &enum_suffix)) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string_view next_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 1]));
        if (!(next_trimmed.starts_with("elseif ") || next_trimmed == "else" ||
              next_trimmed == "end")) {
            continue;
        }
        if (GetLuaLineIndent((*lines)[line_index + 1]) != indent) {
            continue;
        }

        std::string target_lhs;
        std::string text_prefix;
        const auto consider_neighbor = [&](std::size_t probe) -> bool {
            std::string lhs;
            std::string text;
            if (!parse_text_assignment((*lines)[probe], &lhs, &text)) {
                return true;
            }
            const std::size_t last_underscore = text.rfind('_');
            if (last_underscore == std::string::npos || last_underscore == 0) {
                return true;
            }
            if (!target_lhs.empty() && target_lhs != lhs) {
                return false;
            }
            const std::string prefix = text.substr(0, last_underscore + 1);
            if (!text_prefix.empty() && text_prefix != prefix) {
                return false;
            }
            target_lhs = std::move(lhs);
            text_prefix = std::move(prefix);
            return true;
        };

        bool blocked = false;
        for (std::size_t probe = line_index; probe > 0 && line_index - probe < 8;) {
            --probe;
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (trimmed == "end" || trimmed.starts_with("if ")) {
                break;
            }
            if (!consider_neighbor(probe)) {
                blocked = true;
                break;
            }
        }
        if (blocked) {
            continue;
        }
        for (std::size_t probe = line_index + 1;
             probe < lines->size() && probe <= line_index + 8;
             ++probe) {
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (trimmed == "end" || trimmed.starts_with("if ")) {
                break;
            }
            if (!consider_neighbor(probe)) {
                blocked = true;
                break;
            }
        }
        if (blocked || target_lhs.empty() || text_prefix.empty()) {
            continue;
        }

        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + target_lhs + " = \"" + text_prefix + enum_suffix + "\"");
        changed = true;
        ++line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaNegativeOrPredicateReturnsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 4) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        const std::string_view if_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        if (GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 3]) != indent) {
            continue;
        }

        const std::string_view first_return = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 1]));
        const std::string_view end_line = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 2]));
        const std::string_view second_return = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 3]));
        if (first_return != "return false" || end_line != "end" ||
            second_return != "return true") {
            continue;
        }

        const std::string condition = std::string(refii::lua::TrimLuaWhitespace(
            if_trimmed.substr(3, if_trimmed.size() - 8)));
        if (condition.find(" and ") != std::string::npos) {
            continue;
        }
        const std::size_t or_pos = condition.find(" or ");
        if (or_pos == std::string::npos ||
            condition.find(" or ", or_pos + 4) != std::string::npos) {
            continue;
        }

        const std::string lhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(0, or_pos)));
        const std::string rhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(or_pos + 4)));
        if (!lhs.starts_with("not ") || !rhs.starts_with("not ") ||
            lhs.find(',') != std::string::npos || rhs.find(',') != std::string::npos) {
            continue;
        }

        (*lines)[line_index] = indent + "if " + lhs + " and " + rhs + " then";
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaBranchReturnIntoFinalReturnAssignmentForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 4) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        const std::string_view if_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        if (GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 3]) != indent) {
            continue;
        }

        const std::string_view branch_return = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 1]));
        const std::string_view end_line = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 2]));
        const std::string_view final_return = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 3]));
        if (!branch_return.starts_with("return ") || end_line != "end" ||
            !final_return.starts_with("return ")) {
            continue;
        }

        const std::string branch_expression = std::string(
            refii::lua::TrimLuaWhitespace(branch_return.substr(7)));
        const std::string final_name = std::string(
            refii::lua::TrimLuaWhitespace(final_return.substr(7)));
        if (branch_expression.empty() || branch_expression == final_name ||
            branch_expression.find(',') != std::string::npos ||
            final_name.find(',') != std::string::npos ||
            !refii::lua::IsValidLuaIdentifier(final_name) ||
            refii::lua::IsRenderTemporaryName(final_name)) {
            continue;
        }

        bool local_seen = false;
        for (std::size_t probe = line_index; probe > 0 && line_index - probe < 64;) {
            --probe;
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                break;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                        &is_local,
                                                        &lhs_names,
                                                        &rhs_expression) &&
                lhs_names.size() == 1 &&
                refii::lua::TrimLuaWhitespace(lhs_names.front()) == final_name) {
                local_seen = is_local || probe_trimmed.starts_with(final_name + " = ");
                break;
            }
        }
        if (!local_seen) {
            continue;
        }

        (*lines)[line_index + 1] = indent + final_name + " = " + branch_expression;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaRepeatedMultiReturnPredicateForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 3) {
        return false;
    }

    const auto parse_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::vector<std::string>* lhs_names_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_names_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            std::string line_copy(line);
            if (!refii::lua::ParseRenderedAssignmentLine(line_copy,
                                                         is_local_out,
                                                         lhs_names_out,
                                                         rhs_out)) {
                return false;
            }
            for (std::string& lhs : *lhs_names_out) {
                lhs = std::string(refii::lua::TrimLuaWhitespace(lhs));
            }
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(*rhs_out));
            return true;
        };

    const auto normalize_expression = [](std::string value) {
        value = std::string(refii::lua::TrimLuaWhitespace(value));
        while (value.size() >= 2 && value.front() == '(' && value.back() == ')') {
            value = std::string(refii::lua::TrimLuaWhitespace(
                std::string_view(value).substr(1, value.size() - 2)));
        }
        return value;
    };

    const auto split_lhs_names = [](std::string_view lhs) -> std::vector<std::string> {
        std::vector<std::string> names;
        std::size_t cursor = 0;
        while (cursor < lhs.size()) {
            const std::size_t comma = lhs.find(',', cursor);
            std::string name(refii::lua::TrimLuaWhitespace(
                comma == std::string_view::npos ? lhs.substr(cursor)
                                                : lhs.substr(cursor, comma - cursor)));
            if (name.empty() || !refii::lua::IsValidLuaIdentifier(name)) {
                names.clear();
                return names;
            }
            names.emplace_back(std::move(name));
            if (comma == std::string_view::npos) {
                break;
            }
            cursor = comma + 1;
        }
        return names;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        if (GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent) {
            continue;
        }

        bool seed_is_local = false;
        std::vector<std::string> seed_lhs_names;
        std::string seed_rhs;
        if (!parse_assignment((*lines)[line_index],
                              &seed_is_local,
                              &seed_lhs_names,
                              &seed_rhs) ||
            !seed_is_local || seed_lhs_names.size() != 1 || seed_rhs.empty()) {
            continue;
        }

        const std::string carried_arg = seed_lhs_names.front();
        if (!refii::lua::IsValidLuaIdentifier(carried_arg) ||
            refii::lua::CountIdentifierUses(seed_rhs, carried_arg) != 0) {
            continue;
        }

        bool call_is_local = false;
        std::vector<std::string> call_lhs_names;
        std::string call_rhs;
        if (!parse_assignment((*lines)[line_index + 1],
                              &call_is_local,
                              &call_lhs_names,
                              &call_rhs) ||
            call_is_local || call_lhs_names.size() < 3 ||
            call_lhs_names.back() != carried_arg) {
            continue;
        }

        const std::string_view if_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 2]));
        if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }
        const std::string repeated_call = std::string(refii::lua::TrimLuaWhitespace(
            if_trimmed.substr(3, if_trimmed.size() - 8)));
        if (repeated_call.empty() || repeated_call.find(" and ") != std::string::npos ||
            repeated_call.find(" or ") != std::string::npos) {
            continue;
        }

        std::string expected_call_from_seed = call_rhs;
        const std::size_t plain_arg_pos = expected_call_from_seed.rfind(carried_arg);
        if (plain_arg_pos == std::string::npos) {
            continue;
        }
        expected_call_from_seed.replace(plain_arg_pos, carried_arg.size(), seed_rhs);
        if (normalize_expression(expected_call_from_seed) !=
            normalize_expression(repeated_call)) {
            continue;
        }

        const std::string predicate_name = call_lhs_names.front();
        if (!refii::lua::IsValidLuaIdentifier(predicate_name)) {
            continue;
        }

        std::string lhs_joined;
        for (std::size_t lhs_index = 0; lhs_index < call_lhs_names.size(); ++lhs_index) {
            if (lhs_index != 0) {
                lhs_joined += ", ";
            }
            lhs_joined += call_lhs_names[lhs_index];
        }

        (*lines)[line_index].clear();
        (*lines)[line_index + 1] =
            indent + "local " + lhs_joined + " = " + repeated_call;
        (*lines)[line_index + 2] = indent + "if " + predicate_name + " then";
        changed = true;
    }

    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        if (GetLuaLineIndent((*lines)[line_index + 1]) != indent ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent) {
            continue;
        }

        const std::string seed_line = std::string(refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index])));
        if (!seed_line.starts_with("local ")) {
            continue;
        }
        const std::size_t seed_equal = seed_line.find('=');
        if (seed_equal == std::string::npos) {
            continue;
        }
        const std::string carried_arg = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(seed_line).substr(6, seed_equal - 6)));
        const std::string seed_rhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(seed_line).substr(seed_equal + 1)));
        if (!refii::lua::IsValidLuaIdentifier(carried_arg) || seed_rhs.empty() ||
            refii::lua::CountIdentifierUses(seed_rhs, carried_arg) != 0) {
            continue;
        }

        const std::string call_line = std::string(refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 1])));
        if (call_line.starts_with("local ")) {
            continue;
        }
        const std::size_t call_equal = call_line.find('=');
        if (call_equal == std::string::npos) {
            continue;
        }
        std::vector<std::string> call_lhs_names =
            split_lhs_names(std::string_view(call_line).substr(0, call_equal));
        const std::string call_rhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(call_line).substr(call_equal + 1)));
        if (call_lhs_names.size() < 3 || call_lhs_names.back() != carried_arg ||
            call_rhs.empty()) {
            continue;
        }

        const std::string if_line = std::string(refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 2])));
        if (!if_line.starts_with("if ") || !if_line.ends_with(" then")) {
            continue;
        }
        const std::string repeated_call = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(if_line).substr(3, if_line.size() - 8)));
        if (repeated_call.empty() || repeated_call.find(" and ") != std::string::npos ||
            repeated_call.find(" or ") != std::string::npos) {
            continue;
        }

        std::string expected_call_from_seed = call_rhs;
        const std::size_t plain_arg_pos = expected_call_from_seed.rfind(carried_arg);
        if (plain_arg_pos == std::string::npos) {
            continue;
        }
        expected_call_from_seed.replace(plain_arg_pos, carried_arg.size(), seed_rhs);
        if (normalize_expression(expected_call_from_seed) !=
            normalize_expression(repeated_call)) {
            continue;
        }

        std::string lhs_joined;
        for (std::size_t lhs_index = 0; lhs_index < call_lhs_names.size(); ++lhs_index) {
            if (lhs_index != 0) {
                lhs_joined += ", ";
            }
            lhs_joined += call_lhs_names[lhs_index];
        }

        (*lines)[line_index].clear();
        (*lines)[line_index + 1] =
            indent + "local " + lhs_joined + " = " + repeated_call;
        (*lines)[line_index + 2] = indent + "if " + call_lhs_names.front() + " then";
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaSequentialPredicateBodyToElseForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 6) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string_view header = trim_line((*lines)[line_index]);
        if (!header.starts_with("if ") || !header.ends_with(" then")) {
            continue;
        }
        const std::string predicate = std::string(refii::lua::TrimLuaWhitespace(
            header.substr(3, header.size() - 8)));
        if (!refii::lua::IsValidLuaIdentifier(predicate)) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        for (std::size_t offset = 1; offset <= 3; ++offset) {
            if (GetLuaLineIndent((*lines)[line_index + offset]) != indent) {
                goto next_candidate;
            }
        }

        {
            const std::string_view call_line = trim_line((*lines)[line_index + 1]);
            const std::string_view first_end = trim_line((*lines)[line_index + 2]);
            const std::string_view next_line = trim_line((*lines)[line_index + 3]);
            if (first_end != "end" || !next_line.starts_with("if ") ||
                call_line.empty() || call_line.starts_with("return") ||
                call_line == "break" || call_line.find('=') != std::string_view::npos ||
                call_line.find('(') == std::string_view::npos ||
                !call_line.ends_with(')')) {
                goto next_candidate;
            }

            int depth = 0;
            for (std::size_t probe = line_index + 3;
                 probe < std::min(lines->size(), line_index + 48);
                 ++probe) {
                const std::string_view probe_trimmed = trim_line((*lines)[probe]);
                if (probe_trimmed.empty()) {
                    continue;
                }
                if (depth == 0 && probe_trimmed == "end") {
                    (*lines)[line_index + 2] = indent + "else";
                    lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(probe),
                                  indent + "end");
                    changed = true;
                    line_index = probe;
                    goto next_candidate;
                }
                depth += refii::lua::GetRenderedLuaBlockDelta(probe_trimmed);
                if (depth < 0) {
                    break;
                }
            }
        }

    next_candidate:
        continue;
    }

    return changed;
}

bool NormalizeRenderedLuaWhileTrueYieldGuardBreakForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 5) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        if (trim_line((*lines)[line_index]) != "while true do") {
            continue;
        }
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string branch_indent = GetLuaLineIndent((*lines)[line_index + 1]);

        {
            const std::string_view guard = trim_line((*lines)[line_index + 1]);
            const std::string_view yield_line = trim_line((*lines)[line_index + 2]);
            const std::string_view inner_end = trim_line((*lines)[line_index + 3]);
            const std::string_view outer_end = trim_line((*lines)[line_index + 4]);
            if (!guard.starts_with("if not ") || !guard.ends_with(" then") ||
                yield_line != "coroutine.yield()" ||
                inner_end != "end" || outer_end != "end") {
                goto next_candidate;
            }

            (*lines)[line_index + 3] = branch_indent + "else";
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4),
                          branch_indent + "break");
            changed = true;
            line_index += 5;
        }

    next_candidate:
        continue;
    }

    return changed;
}

bool NormalizeRenderedLuaTalkerPartnerAnimationElseForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 12) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto is_simple_local_assignment_to_self_get_anim =
        [](std::string_view line, std::string* local_name_out) {
            if (local_name_out == nullptr) {
                return false;
            }
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            if (!trimmed.starts_with("local ") ||
                trimmed.find(" = self:GetAnim(") == std::string_view::npos ||
                !trimmed.ends_with(')')) {
                return false;
            }
            const std::size_t equals_pos = trimmed.find(" = ");
            if (equals_pos == std::string_view::npos) {
                return false;
            }
            const std::string local_name = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(6, equals_pos - 6)));
            if (!refii::lua::IsValidLuaIdentifier(local_name)) {
                return false;
            }
            *local_name_out = local_name;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 11 < lines->size(); ++line_index) {
        const std::string_view talker_guard = trim_line((*lines)[line_index]);
        if (!talker_guard.starts_with("if ") ||
            talker_guard.find(":IsTalker(") == std::string_view::npos ||
            !talker_guard.ends_with(" then")) {
            continue;
        }

        const std::size_t partner_line = line_index + 1;
        if (trim_line((*lines)[partner_line]) != "if partner then") {
            continue;
        }

        const std::size_t play_line = line_index + 2;
        if (trim_line((*lines)[play_line]).find(":PlayConversationItem(") ==
            std::string_view::npos) {
            continue;
        }

        const std::size_t while_line = line_index + 3;
        if (trim_line((*lines)[while_line]) != "while true do") {
            continue;
        }

        const std::size_t wait_guard_line = line_index + 4;
        const std::string_view wait_guard = trim_line((*lines)[wait_guard_line]);
        if (!wait_guard.starts_with("if not ") ||
            wait_guard.find("ScriptFunction.IsAvailableToSayLine(") ==
                std::string_view::npos ||
            !wait_guard.ends_with(" then")) {
            continue;
        }

        const std::size_t yield_line = line_index + 5;
        const std::size_t wait_if_end_line = line_index + 6;
        const std::size_t while_end_line = line_index + 7;
        const std::size_t partner_end_line = line_index + 8;
        const std::size_t talker_end_line = line_index + 9;
        if (trim_line((*lines)[yield_line]) != "coroutine.yield()" ||
            trim_line((*lines)[wait_if_end_line]) != "end" ||
            trim_line((*lines)[while_end_line]) != "end" ||
            trim_line((*lines)[partner_end_line]) != "end" ||
            trim_line((*lines)[talker_end_line]) != "end") {
            continue;
        }

        const std::size_t anim_line = line_index + 10;
        const std::size_t play_anim_line = line_index + 11;
        std::string anim_local;
        if (!is_simple_local_assignment_to_self_get_anim((*lines)[anim_line],
                                                         &anim_local)) {
            continue;
        }
        const std::string_view play_anim_trimmed = trim_line((*lines)[play_anim_line]);
        if (play_anim_trimmed != "self:PlayAnimation(" + anim_local + ")") {
            continue;
        }

        const std::string talker_indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string while_indent = GetLuaLineIndent((*lines)[while_line]);
        const std::string partner_indent = GetLuaLineIndent((*lines)[partner_line]);
        const std::string anim_indent = GetLuaLineIndent((*lines)[anim_line]);
        const std::string wait_expression = std::string(refii::lua::TrimLuaWhitespace(
            wait_guard.substr(7, wait_guard.size() - 7 - 5)));

        std::vector<std::string> replacement;
        replacement.reserve(11);
        replacement.push_back((*lines)[line_index]);
        replacement.push_back((*lines)[partner_line]);
        replacement.push_back((*lines)[play_line]);
        replacement.push_back(while_indent + "while not " + wait_expression + " do");
        replacement.push_back((*lines)[yield_line]);
        replacement.push_back((*lines)[while_end_line]);
        replacement.push_back(partner_indent + "else");
        replacement.push_back(anim_indent + "local " + anim_local +
                              " = self:GetAnim(self.Entity)");
        replacement.push_back(anim_indent + "self:PlayAnimation(" + anim_local + ")");
        replacement.push_back(partner_indent + "end");
        replacement.push_back(talker_indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(play_anim_line + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        line_index += replacement.size();
    }

    return changed;
}

bool NormalizeRenderedLuaTimingFrameDeltaGuardScaffolds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs_expression) ||
                lhs_names.size() != 1) {
                return false;
            }
            *is_local_out = is_local;
            *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        bool delta_is_local = false;
        std::string delta_lhs;
        std::string delta_rhs;
        if (!parse_single_assignment((*lines)[line_index],
                                     &delta_is_local,
                                     &delta_lhs,
                                     &delta_rhs) ||
            !delta_is_local || delta_rhs.find("Timing.GetWorldFrame()") == std::string::npos) {
            continue;
        }

        const std::size_t minus_pos = delta_rhs.rfind(" - ");
        if (minus_pos == std::string::npos) {
            continue;
        }
        const std::string frame_call =
            std::string(refii::lua::TrimLuaWhitespace(delta_rhs.substr(0, minus_pos)));
        const std::string state_name =
            std::string(refii::lua::TrimLuaWhitespace(delta_rhs.substr(minus_pos + 3)));
        if (frame_call != "Timing.GetWorldFrame()" ||
            !refii::lua::IsValidLuaIdentifier(state_name)) {
            continue;
        }

        bool shadow_is_local = false;
        std::string shadow_lhs;
        std::string shadow_rhs;
        if (!parse_single_assignment((*lines)[line_index + 1],
                                     &shadow_is_local,
                                     &shadow_lhs,
                                     &shadow_rhs) ||
            !shadow_is_local || shadow_lhs != state_name || shadow_rhs != frame_call ||
            GetLuaLineIndent((*lines)[line_index]) !=
                GetLuaLineIndent((*lines)[line_index + 1])) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string_view if_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index + 2]));
        if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then") ||
            GetLuaLineIndent((*lines)[line_index + 2]) != indent) {
            continue;
        }
        const std::string condition = std::string(refii::lua::TrimLuaWhitespace(
            if_trimmed.substr(3, if_trimmed.size() - 3 - 5)));
        const std::string duplicated_delta = state_name + " - " + state_name;
        const std::size_t compare_pos = condition.find(" <= ");
        if (compare_pos == std::string::npos ||
            refii::lua::TrimLuaWhitespace(condition.substr(compare_pos + 4)) !=
                duplicated_delta) {
            continue;
        }
        const std::string threshold =
            std::string(refii::lua::TrimLuaWhitespace(condition.substr(0, compare_pos)));
        if (threshold.empty()) {
            continue;
        }

        std::size_t self_assign_line = std::string::npos;
        std::size_t guard_end_line = std::string::npos;
        std::size_t empty_inner_if_end_line = std::string::npos;
        for (std::size_t probe = line_index + 3;
             probe < lines->size() && probe <= line_index + 10;
             ++probe) {
            const std::string_view probe_trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent((*lines)[probe]) == indent && probe_trimmed == "end" &&
                self_assign_line != std::string::npos) {
                guard_end_line = probe;
                break;
            }
            if (empty_inner_if_end_line == std::string::npos &&
                probe + 1 < lines->size() && probe_trimmed.starts_with("if not ") &&
                probe_trimmed.ends_with(" then")) {
                const std::string_view next_trimmed =
                    refii::lua::TrimLuaWhitespace(
                        refii::lua::StripLuaLineComment((*lines)[probe + 1]));
                if (next_trimmed == "end") {
                    empty_inner_if_end_line = probe + 1;
                }
            }
            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (parse_single_assignment((*lines)[probe],
                                        &probe_is_local,
                                        &probe_lhs,
                                        &probe_rhs) &&
                !probe_is_local && probe_lhs == state_name && probe_rhs == state_name) {
                self_assign_line = probe;
            }
        }
        if (self_assign_line == std::string::npos || guard_end_line == std::string::npos) {
            continue;
        }

        (*lines)[line_index] = indent + "if " + threshold + " <= " + delta_rhs + " then";
        (*lines)[self_assign_line] =
            GetLuaLineIndent((*lines)[self_assign_line]) + state_name + " = " + frame_call;

        std::size_t erase_start = line_index + 1;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(erase_start),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        guard_end_line -= 2;
        if (empty_inner_if_end_line != std::string::npos &&
            empty_inner_if_end_line >= line_index + 3) {
            empty_inner_if_end_line -= 2;
        }

        if (guard_end_line + 3 < lines->size()) {
            const std::string_view trailing_if = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[guard_end_line + 1]));
            const std::string_view trailing_break = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[guard_end_line + 2]));
            const std::string_view trailing_end = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[guard_end_line + 3]));
            const std::string trailing_guard =
                "if " + threshold + " <= " + frame_call + " then";
            const std::string reversed_trailing_guard =
                "if " + frame_call + " >= " + threshold + " then";
            if (GetLuaLineIndent((*lines)[guard_end_line + 1]) == indent &&
                (trailing_if == trailing_guard ||
                 trailing_if == reversed_trailing_guard) &&
                trailing_break == "break" &&
                GetLuaLineIndent((*lines)[guard_end_line + 3]) == indent &&
                trailing_end == "end") {
                if (empty_inner_if_end_line != std::string::npos &&
                    empty_inner_if_end_line < lines->size()) {
                    const std::string inner_indent =
                        GetLuaLineIndent((*lines)[empty_inner_if_end_line]);
                    lines->insert(lines->begin() +
                                      static_cast<std::ptrdiff_t>(empty_inner_if_end_line),
                                  inner_indent + "  break");
                    if (guard_end_line >= empty_inner_if_end_line) {
                        ++guard_end_line;
                    }
                }
                lines->erase(lines->begin() +
                                 static_cast<std::ptrdiff_t>(guard_end_line + 1),
                             lines->begin() +
                                 static_cast<std::ptrdiff_t>(guard_end_line + 4));
            }
        }
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaPairsCallLocalTableSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                     &is_local,
                                                     &lhs_names,
                                                     &rhs) ||
            !is_local || lhs_names.size() != 1 || rhs.find('(') == std::string::npos ||
            rhs == "{}") {
            continue;
        }

        const std::string lhs =
            std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
        if (!refii::lua::IsValidLuaIdentifier(lhs) ||
            refii::lua::CountIdentifierUses(rhs, lhs) != 0) {
            continue;
        }

        std::size_t loop_line = line_index + 1;
        while (loop_line < lines->size() &&
               refii::lua::TrimLuaWhitespace(
                   refii::lua::StripLuaLineComment((*lines)[loop_line]))
                   .empty()) {
            ++loop_line;
        }
        if (loop_line >= lines->size() ||
            GetLuaLineIndent((*lines)[loop_line]) != GetLuaLineIndent((*lines)[line_index])) {
            continue;
        }
        const std::string_view loop_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[loop_line]));
        if (!loop_trimmed.starts_with("for ") ||
            loop_trimmed.find("pairs(" + lhs + ")") == std::string_view::npos) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] = indent + "local " + lhs + " = {}";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + lhs + " = " + rhs);
        changed = true;
        ++line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaInlineLoopActionSetCurrentActionLocals(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        constexpr std::string_view kPrefix =
            "if Action.SetCurrentAction(self.Entity, ";
        constexpr std::string_view kSuffix = ") then";
        if (!trimmed.starts_with(kPrefix) || !trimmed.ends_with(kSuffix)) {
            continue;
        }

        const std::string table_literal = std::string(refii::lua::TrimLuaWhitespace(
            trimmed.substr(kPrefix.size(),
                           trimmed.size() - kPrefix.size() - kSuffix.size())));
        if (table_literal.size() < 2 || table_literal.front() != '{' ||
            table_literal.back() != '}' ||
            table_literal.find("Type = EScriptableAction.LOOP") == std::string::npos ||
            table_literal.find("IntoAction") == std::string::npos ||
            table_literal.find("LoopAction") == std::string::npos ||
            table_literal.find("OutOfAction") == std::string::npos ||
            table_literal.find("NumLoops") == std::string::npos) {
            continue;
        }

        const std::string local_name =
            table_literal.find("Skidmarks") != std::string::npos ? "worms_loop"
                                                                 : "loop_action";
        bool conflicts = false;
        for (std::size_t probe = 0; probe < lines->size(); ++probe) {
            if (probe == line_index) {
                continue;
            }
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (refii::lua::CountIdentifierUses(probe_trimmed, local_name) != 0) {
                conflicts = true;
                break;
            }
        }
        if (conflicts) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] = indent + "local " + local_name + " = " + table_literal;
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + "if Action.SetCurrentAction(self.Entity, " +
                          local_name + ") then");
        changed = true;
        ++line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaInlineTableInsertRecordLocals(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        constexpr std::string_view kPrefix = "table.insert(";
        if (!trimmed.starts_with(kPrefix) || !trimmed.ends_with(")")) {
            continue;
        }
        const std::size_t literal_start = trimmed.find(", {");
        if (literal_start == std::string_view::npos) {
            continue;
        }
        const std::string table_name = std::string(refii::lua::TrimLuaWhitespace(
            trimmed.substr(kPrefix.size(), literal_start - kPrefix.size())));
        const std::string literal = std::string(refii::lua::TrimLuaWhitespace(
            trimmed.substr(literal_start + 2, trimmed.size() - literal_start - 3)));
        if (!refii::lua::IsValidLuaIdentifier(table_name) ||
            literal.size() < 2 || literal.front() != '{' || literal.back() != '}' ||
            literal.find("exit_marker") == std::string::npos ||
            literal.find("distance") == std::string::npos) {
            continue;
        }

        const std::string local_name = "dist_marker_pair";
        bool conflicts = false;
        for (const std::string& line : *lines) {
            const std::string_view line_trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            if (refii::lua::CountIdentifierUses(line_trimmed, local_name) != 0) {
                conflicts = true;
                break;
            }
        }
        if (conflicts) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] = indent + "local " + local_name + " = " + literal;
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + "table.insert(" + table_name + ", " + local_name + ")");
        changed = true;
        ++line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaRepeatedSubtractThresholdComparisons(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 2) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 1; line_index < lines->size(); ++line_index) {
        bool previous_is_local = false;
        std::vector<std::string> previous_lhs_names;
        std::string previous_rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index - 1],
                                                     &previous_is_local,
                                                     &previous_lhs_names,
                                                     &previous_rhs) ||
            previous_lhs_names.size() != 1 || previous_is_local) {
            continue;
        }
        const std::string lhs =
            std::string(refii::lua::TrimLuaWhitespace(previous_lhs_names.front()));
        previous_rhs = std::string(refii::lua::TrimLuaWhitespace(previous_rhs));
        if (previous_rhs.size() >= 2 && previous_rhs.front() == '(' &&
            previous_rhs.back() == ')') {
            previous_rhs = std::string(refii::lua::TrimLuaWhitespace(
                std::string_view(previous_rhs).substr(1, previous_rhs.size() - 2)));
        }
        const std::string subtract_prefix = lhs + " - ";
        if (!refii::lua::IsValidLuaIdentifier(lhs) ||
            !previous_rhs.starts_with(subtract_prefix)) {
            continue;
        }
        const std::string subtract_rhs = std::string(
            refii::lua::TrimLuaWhitespace(previous_rhs.substr(subtract_prefix.size())));
        if (subtract_rhs.empty()) {
            continue;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then")) {
            continue;
        }
        const std::string condition = std::string(refii::lua::TrimLuaWhitespace(
            trimmed.substr(3, trimmed.size() - 3 - 5)));
        const std::string repeated = lhs + " - " + subtract_rhs + " <= 0";
        if (condition != repeated ||
            GetLuaLineIndent((*lines)[line_index]) !=
                GetLuaLineIndent((*lines)[line_index - 1])) {
            continue;
        }

        (*lines)[line_index] = GetLuaLineIndent((*lines)[line_index]) +
                               "if " + lhs + " <= 0 then";
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaInverseDistanceLocalTiming(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                     &is_local,
                                                     &lhs_names,
                                                     &rhs) ||
            !is_local || lhs_names.size() != 1) {
            continue;
        }
        const std::string lhs =
            std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
        rhs = std::string(refii::lua::TrimLuaWhitespace(rhs));
        if (!refii::lua::IsValidLuaIdentifier(lhs)) {
            continue;
        }
        std::string call_expression;
        constexpr std::string_view kParenPrefix = "(1) / ";
        constexpr std::string_view kPlainPrefix = "1 / ";
        if (std::string_view(rhs).starts_with(kParenPrefix)) {
            call_expression = std::string(refii::lua::TrimLuaWhitespace(
                std::string_view(rhs).substr(kParenPrefix.size())));
        } else if (std::string_view(rhs).starts_with(kPlainPrefix)) {
            call_expression = std::string(refii::lua::TrimLuaWhitespace(
                std::string_view(rhs).substr(kPlainPrefix.size())));
        } else {
            continue;
        }
        if (!call_expression.starts_with("GetDistanceBetweenEntities(") ||
            call_expression.find(lhs) != std::string::npos) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] = indent + "local " + lhs + " = " + call_expression;
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + lhs + " = 1 / " + lhs);
        changed = true;
        ++line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaIdleActionTimerScaffolds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto extract_base_delay =
        [](std::string_view text, std::string* delay_out) {
            if (delay_out == nullptr) {
                return false;
            }
            const std::size_t marker = text.find("+ idle_action");
            if (marker == std::string_view::npos) {
                return false;
            }
            std::size_t end = marker;
            while (end > 0 &&
                   (std::isspace(static_cast<unsigned char>(text[end - 1])) ||
                    text[end - 1] == ')')) {
                --end;
            }
            std::size_t begin = end;
            while (begin > 0 && std::isdigit(static_cast<unsigned char>(text[begin - 1]))) {
                --begin;
            }
            if (begin == end) {
                return false;
            }
            *delay_out = std::string(text.substr(begin, end - begin));
            return !delay_out->empty();
        };

    const auto is_generated_callable =
        [](std::string_view trimmed) {
            if (trimmed.starts_with("Action.IsPerformingAnyAction(self.Entity)(")) {
                return true;
            }
            if (trimmed.size() < 3 || trimmed.front() != 'r') {
                return false;
            }
            std::size_t index = 1;
            while (index < trimmed.size() &&
                   std::isdigit(static_cast<unsigned char>(trimmed[index]))) {
                ++index;
            }
            return index > 1 && index < trimmed.size() && trimmed[index] == '(';
        };

    const auto function_start_before =
        [&](std::size_t line_index) {
            for (std::size_t probe = line_index + 1; probe > 0; --probe) {
                const std::size_t index = probe - 1;
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[index]));
                if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    return index;
                }
            }
            return std::size_t{0};
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (trimmed.find("(self.Entity,") == std::string_view::npos ||
            trimmed.find("idle_action") == std::string_view::npos ||
            !is_generated_callable(trimmed)) {
            continue;
        }

        std::string base_delay;
        if (!extract_base_delay(trimmed, &base_delay)) {
            continue;
        }

        const std::size_t function_start = function_start_before(line_index);
        bool has_idle_timer_guard = false;
        bool has_idle_action_seed = false;
        bool has_action_wait_loop = false;
        bool has_stand_idle_variant_table = false;
        for (std::size_t probe = function_start; probe < line_index; ++probe) {
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.find("self.IdleTimer:GetTime()") != std::string_view::npos) {
                has_idle_timer_guard = true;
            }
            if (probe_trimmed.find("idle_action = ActionOfType(") != std::string_view::npos) {
                has_idle_action_seed = true;
            }
            if (probe_trimmed == "while Action.IsPerformingAnyAction(self.Entity) do") {
                has_action_wait_loop = true;
            }
            if (probe_trimmed.find("\"StandIdle003\"") != std::string_view::npos) {
                has_stand_idle_variant_table = true;
            }
        }
        if (!has_idle_timer_guard || !has_idle_action_seed || !has_action_wait_loop) {
            continue;
        }

        const std::string random_limit =
            (base_delay == "5" && has_stand_idle_variant_table) ? "15" : base_delay;
        (*lines)[line_index] = GetLuaLineIndent((*lines)[line_index]) +
                               "self.IdleTimer:SetTime(" + base_delay +
                               " + GetRandomNumber(" + random_limit + "))";

        if (line_index >= 2) {
            const std::string_view maybe_else = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[line_index - 2]));
            const std::string_view maybe_end = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[line_index - 1]));
            if (maybe_else == "else" && maybe_end == "end" &&
                GetLuaLineIndent((*lines)[line_index - 2]) ==
                    GetLuaLineIndent((*lines)[line_index - 1])) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index - 2));
                --line_index;
            }
        }
        changed = true;
    }

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (trimmed != "self.IdleTimer:SetTime(GetRandomNumber(15))") {
            continue;
        }

        const std::size_t function_start = function_start_before(line_index);
        bool has_idle_timer_guard = false;
        bool has_idle_action_seed = false;
        bool has_action_wait_loop = false;
        bool has_stand_idle_variant_table = false;
        for (std::size_t probe = function_start; probe < line_index; ++probe) {
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.find("self.IdleTimer:GetTime()") != std::string_view::npos) {
                has_idle_timer_guard = true;
            }
            if (probe_trimmed.find("idle_action = ActionOfType(") != std::string_view::npos) {
                has_idle_action_seed = true;
            }
            if (probe_trimmed == "while Action.IsPerformingAnyAction(self.Entity) do") {
                has_action_wait_loop = true;
            }
            if (probe_trimmed.find("\"StandIdle003\"") != std::string_view::npos) {
                has_stand_idle_variant_table = true;
            }
        }
        if (!has_idle_timer_guard || !has_idle_action_seed || !has_action_wait_loop ||
            !has_stand_idle_variant_table) {
            continue;
        }

        (*lines)[line_index] = GetLuaLineIndent((*lines)[line_index]) +
                               "self.IdleTimer:SetTime(5 + GetRandomNumber(15))";
        changed = true;
    }

    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                     &is_local,
                                                     &lhs_names,
                                                     &rhs) ||
            !is_local || lhs_names.size() != 1 ||
            rhs != "self.IdleTimer:GetTime()") {
            continue;
        }

        const std::string local_name =
            std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
        if (local_name.empty() || local_name.front() != 'r') {
            continue;
        }

        std::size_t guard_line = line_index + 1;
        while (guard_line < lines->size() &&
               refii::lua::TrimLuaWhitespace(
                   refii::lua::StripLuaLineComment((*lines)[guard_line]))
                   .empty()) {
            ++guard_line;
        }
        if (guard_line >= lines->size() ||
            GetLuaLineIndent((*lines)[guard_line]) != GetLuaLineIndent((*lines)[line_index])) {
            continue;
        }
        const std::string_view guard_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[guard_line]));
        if (!guard_trimmed.starts_with("if ") ||
            guard_trimmed.find("self.IdleTimer:GetTime()") == std::string_view::npos ||
            guard_trimmed.find(local_name) != std::string_view::npos) {
            continue;
        }

        bool used_later = false;
        for (std::size_t probe = guard_line + 1; probe < lines->size(); ++probe) {
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                break;
            }
            if (refii::lua::CountIdentifierUses((*lines)[probe], local_name) != 0) {
                used_later = true;
                break;
            }
        }
        if (used_later) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        --line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaSingletonLiteralThenIndexPatchChain(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                        &is_local,
                                                        &lhs_names,
                                                        &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty() && !rhs_out->empty();
            }

            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            const std::size_t equals_pos = trimmed.find('=');
            if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                equals_pos + 1 >= trimmed.size() ||
                trimmed.find("==") != std::string_view::npos ||
                trimmed.find("~=") != std::string_view::npos ||
                trimmed.find("<=") != std::string_view::npos ||
                trimmed.find(">=") != std::string_view::npos) {
                return false;
            }
            std::string lhs =
                std::string(refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
            const std::string rhs =
                std::string(refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
            if (lhs.empty() || rhs.empty()) {
                return false;
            }
            *is_local_out = lhs.starts_with("local ");
            if (*is_local_out) {
                lhs = std::string(refii::lua::TrimLuaWhitespace(
                    std::string_view(lhs).substr(6)));
            }
            *lhs_out = lhs;
            *rhs_out = rhs;
            return !lhs_out->empty();
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!parse_assignment((*lines)[line_index],
                              &seed_is_local,
                              &seed_lhs,
                              &seed_rhs) ||
            seed_is_local || seed_rhs.size() < 5 || seed_rhs.front() != '{' ||
            seed_rhs.back() != '}' || seed_rhs == "{}" ||
            seed_rhs.find('=') == std::string::npos ||
            seed_lhs.find('(') != std::string::npos) {
            continue;
        }

        std::vector<std::string> extra_entries;
        std::vector<std::size_t> erase_lines;
        std::size_t probe = line_index + 1;
        while (probe < lines->size() && extra_entries.size() < 8) {
            const std::string_view probe_trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                ++probe;
                continue;
            }
            if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed) ||
                GetLuaLineIndent((*lines)[probe]) != GetLuaLineIndent((*lines)[line_index])) {
                break;
            }

            bool patch_is_local = false;
            std::string patch_lhs;
            std::string patch_rhs;
            if (!parse_assignment((*lines)[probe],
                                  &patch_is_local,
                                  &patch_lhs,
                                  &patch_rhs) ||
                patch_is_local || patch_rhs.empty()) {
                break;
            }
            const std::string prefix = seed_lhs + "[";
            if (!patch_lhs.starts_with(prefix) || patch_lhs.back() != ']') {
                break;
            }
            const std::string key =
                patch_lhs.substr(seed_lhs.size() + 1,
                                 patch_lhs.size() - seed_lhs.size() - 2);
            if (refii::lua::TrimLuaWhitespace(key).empty()) {
                break;
            }
            extra_entries.push_back("[" + key + "] = " + patch_rhs);
            erase_lines.push_back(probe);
            ++probe;
        }

        if (extra_entries.empty()) {
            continue;
        }

        std::string body = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(seed_rhs).substr(1, seed_rhs.size() - 2)));
        for (const std::string& entry : extra_entries) {
            if (!body.empty()) {
                body += ", ";
            }
            body += entry;
        }

        (*lines)[line_index] =
            GetLuaLineIndent((*lines)[line_index]) + seed_lhs + " = {" + body + "}";
        for (std::size_t erase_index = erase_lines.size(); erase_index-- > 0;) {
            lines->erase(lines->begin() +
                         static_cast<std::ptrdiff_t>(erase_lines[erase_index]));
        }
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaBareIndexPatchChainToLiteralAssignment(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs_expression) ||
                lhs_names.size() != 1) {
                const std::string_view trimmed =
                    refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
                const std::size_t equals_pos = trimmed.find('=');
                if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                    equals_pos + 1 >= trimmed.size() ||
                    trimmed.find("==") != std::string_view::npos ||
                    trimmed.find("~=") != std::string_view::npos ||
                    trimmed.find("<=") != std::string_view::npos ||
                    trimmed.find(">=") != std::string_view::npos) {
                    return false;
                }
                const std::string lhs = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
                const std::string rhs = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
                if (lhs.empty() || rhs.empty()) {
                    return false;
                }
                *is_local_out = lhs.starts_with("local ");
                *lhs_out = *is_local_out
                               ? std::string(refii::lua::TrimLuaWhitespace(
                                     std::string_view(lhs).substr(6)))
                               : lhs;
                *rhs_out = rhs;
                return !lhs_out->empty();
            }
            *is_local_out = is_local;
            *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto parse_index_lhs =
        [](std::string_view lhs, std::string* base_out, std::string* key_out) {
            if (base_out == nullptr || key_out == nullptr) {
                return false;
            }
            lhs = refii::lua::TrimLuaWhitespace(lhs);
            if (lhs.empty() || lhs.back() != ']') {
                return false;
            }
            const std::size_t open = lhs.rfind('[');
            if (open == std::string_view::npos || open == 0 || open + 1 >= lhs.size() - 1) {
                return false;
            }
            const std::string base =
                std::string(refii::lua::TrimLuaWhitespace(lhs.substr(0, open)));
            const std::string key = std::string(refii::lua::TrimLuaWhitespace(
                lhs.substr(open + 1, lhs.size() - open - 2)));
            if (base.empty() || key.empty() || base.find('(') != std::string::npos ||
                base.find('[') != std::string::npos) {
                return false;
            }
            *base_out = base;
            *key_out = key;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        if (!parse_assignment((*lines)[line_index],
                              &first_is_local,
                              &first_lhs,
                              &first_rhs) ||
            first_is_local || first_rhs.empty()) {
            continue;
        }

        std::string base;
        std::string key;
        if (!parse_index_lhs(first_lhs, &base, &key)) {
            continue;
        }

        std::vector<std::string> entries;
        entries.push_back("[" + key + "] = " + first_rhs);
        std::vector<std::size_t> erase_lines;
        std::size_t probe = line_index + 1;
        while (probe < lines->size() && entries.size() < 8) {
            const std::string_view probe_trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                ++probe;
                continue;
            }
            if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed) ||
                GetLuaLineIndent((*lines)[probe]) != GetLuaLineIndent((*lines)[line_index])) {
                break;
            }

            bool patch_is_local = false;
            std::string patch_lhs;
            std::string patch_rhs;
            if (!parse_assignment((*lines)[probe],
                                  &patch_is_local,
                                  &patch_lhs,
                                  &patch_rhs) ||
                patch_is_local || patch_rhs.empty()) {
                break;
            }
            std::string patch_base;
            std::string patch_key;
            if (!parse_index_lhs(patch_lhs, &patch_base, &patch_key) || patch_base != base) {
                break;
            }
            entries.push_back("[" + patch_key + "] = " + patch_rhs);
            erase_lines.push_back(probe);
            ++probe;
        }

        if (entries.size() < 2) {
            continue;
        }

        std::string body;
        for (const std::string& entry : entries) {
            if (!body.empty()) {
                body += ", ";
            }
            body += entry;
        }
        (*lines)[line_index] =
            GetLuaLineIndent((*lines)[line_index]) + base + " = {" + body + "}";
        for (std::size_t erase_index = erase_lines.size(); erase_index-- > 0;) {
            lines->erase(lines->begin() +
                         static_cast<std::ptrdiff_t>(erase_lines[erase_index]));
        }
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaNearExactSingletonIndexTableAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto try_parse_singleton_index_entry =
        [](std::string_view entry_text,
           std::string* key_out,
           std::string* value_out) -> bool {
            if (key_out == nullptr || value_out == nullptr) {
                return false;
            }
            *key_out = std::string();
            *value_out = std::string();

            entry_text = refii::lua::TrimLuaWhitespace(entry_text);
            if (entry_text.empty() || entry_text.front() != '[') {
                return false;
            }
            if (entry_text.back() == ',') {
                entry_text = refii::lua::TrimLuaWhitespace(
                    entry_text.substr(0, entry_text.size() - 1));
            }
            if (entry_text.empty() || entry_text.front() != '[') {
                return false;
            }

            const std::size_t close_bracket = entry_text.find(']');
            if (close_bracket == std::string_view::npos || close_bracket <= 1) {
                return false;
            }
            const std::string_view key_text = refii::lua::TrimLuaWhitespace(
                entry_text.substr(1, close_bracket - 1));
            if (key_text.empty()) {
                return false;
            }

            const std::size_t equals_pos = entry_text.find('=', close_bracket + 1);
            if (equals_pos == std::string_view::npos || equals_pos + 1 >= entry_text.size()) {
                return false;
            }
            const std::string_view value_text = refii::lua::TrimLuaWhitespace(
                entry_text.substr(equals_pos + 1));
            if (value_text.empty()) {
                return false;
            }

            *key_out = std::string(key_text);
            *value_out = std::string(value_text);
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string& line = (*lines)[line_index];
        if (line.find("--") != std::string::npos) {
            continue;
        }

        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!refii::lua::ParseRenderedAssignmentLine(line, &is_local, &lhs_names, &rhs) ||
            is_local || lhs_names.size() != 1) {
            const std::string_view raw_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment(line));
            const std::size_t eq = raw_trimmed.find('=');
            if (eq == std::string_view::npos || eq == 0 || eq + 1 >= raw_trimmed.size() ||
                raw_trimmed.find("==") != std::string_view::npos) {
                continue;
            }
            const std::string lhs_fallback = std::string(
                refii::lua::TrimLuaWhitespace(raw_trimmed.substr(0, eq)));
            const std::string rhs_fallback = std::string(
                refii::lua::TrimLuaWhitespace(raw_trimmed.substr(eq + 1)));
            if (lhs_fallback.empty() || rhs_fallback.empty() ||
                lhs_fallback.starts_with("local ")) {
                continue;
            }
            lhs_names = {lhs_fallback};
            rhs = rhs_fallback;
        }

        const std::string lhs_trimmed =
            std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
        const std::string rhs_trimmed =
            std::string(refii::lua::TrimLuaWhitespace(rhs));
        if (lhs_trimmed.empty() ||
            lhs_trimmed.find(',') != std::string::npos ||
            (lhs_trimmed.find('.') == std::string::npos &&
             lhs_trimmed.find('[') == std::string::npos) ||
            rhs_trimmed.empty()) {
            continue;
        }

        std::string index_expression;
        std::string value_expression;
        std::size_t multiline_end_line = line_index;
        if (rhs_trimmed.front() == '{' && rhs_trimmed.back() == '}' &&
            rhs_trimmed.size() >= 6) {
            const std::string_view body = refii::lua::TrimLuaWhitespace(
                std::string_view(rhs_trimmed).substr(1, rhs_trimmed.size() - 2));
            if (!try_parse_singleton_index_entry(body, &index_expression, &value_expression)) {
                continue;
            }
        } else if (rhs_trimmed == "{") {
            std::size_t entry_line = lines->size();
            std::size_t close_line = lines->size();
            bool multiple_entries = false;
            const std::size_t kMaxTableLines = 8;
            for (std::size_t probe = line_index + 1;
                 probe < lines->size() && probe <= line_index + kMaxTableLines;
                 ++probe) {
                const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[probe]));
                if (probe_trimmed.empty()) {
                    continue;
                }
                if (probe_trimmed == "}" || probe_trimmed == "},") {
                    close_line = probe;
                    break;
                }
                if (entry_line != lines->size()) {
                    multiple_entries = true;
                    break;
                }
                entry_line = probe;
            }
            if (multiple_entries || entry_line == lines->size() ||
                close_line == lines->size() || close_line <= entry_line) {
                continue;
            }

            const std::string_view entry_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[entry_line]));
            if (!try_parse_singleton_index_entry(entry_trimmed,
                                                 &index_expression,
                                                 &value_expression)) {
                continue;
            }
            multiline_end_line = close_line;
        } else {
            continue;
        }
        if (index_expression.empty() || value_expression.empty()) {
            continue;
        }

        const std::string rewritten = GetLuaLineIndent(line) + lhs_trimmed + "[" +
                                      index_expression + "] = " + value_expression;
        if (rewritten == line) {
            continue;
        }
        line = rewritten;
        if (multiline_end_line > line_index) {
            for (std::size_t clear_line = line_index + 1;
                 clear_line <= multiline_end_line;
                 ++clear_line) {
                (*lines)[clear_line].clear();
            }
        }
        changed = true;
    }

    return changed;
}

bool TrySwapRenderedReturnSelfRightHandComparisonsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_top_level_comparison =
        [](std::string_view value,
           std::size_t* position_out,
           std::string* comparator_out) {
            if (position_out == nullptr || comparator_out == nullptr) {
                return false;
            }
            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            for (std::size_t index = 0; index < value.size(); ++index) {
                const char ch = value[index];
                if (ch == '\'' && !in_double_quote) {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (ch == '"' && !in_single_quote) {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    --paren_depth;
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    --brace_depth;
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    --bracket_depth;
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                for (std::string_view needle : {" <= ", " >= ", " < ", " > "}) {
                    if (value.substr(index).starts_with(needle)) {
                        *position_out = index;
                        *comparator_out = std::string(needle);
                        return true;
                    }
                }
            }
            return false;
        };

    const auto swap_comparator = [](std::string_view comparator) -> std::string {
        if (comparator == " < ") {
            return " > ";
        }
        if (comparator == " > ") {
            return " < ";
        }
        if (comparator == " <= ") {
            return " >= ";
        }
        if (comparator == " >= ") {
            return " <= ";
        }
        return std::string(comparator);
    };

    const auto is_safe_member_chain = [](std::string_view expression) {
        expression = refii::lua::TrimLuaWhitespace(expression);
        if (expression.empty()) {
            return false;
        }
        std::size_t start = 0;
        while (start < expression.size()) {
            const std::size_t dot = expression.find('.', start);
            const std::size_t end = dot == std::string_view::npos ? expression.size() : dot;
            const std::string_view segment = expression.substr(start, end - start);
            if (segment.empty() || !refii::lua::IsValidLuaIdentifier(segment)) {
                return false;
            }
            if (dot == std::string_view::npos) {
                return true;
            }
            start = dot + 1;
        }
        return false;
    };

    const auto is_self_receiver = [](std::string_view expression) {
        expression = refii::lua::TrimLuaWhitespace(expression);
        return expression == "self" || expression == "arg0" ||
               expression.starts_with("self.") || expression.starts_with("arg0.");
    };

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string indent(GetLuaLineIndent(line));
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
        if (!trimmed.starts_with("return ")) {
            continue;
        }

        const std::string_view expression = refii::lua::TrimLuaWhitespace(trimmed.substr(7));
        if (expression.empty() || expression.find(',') != std::string_view::npos) {
            continue;
        }

        std::size_t compare_position = std::string::npos;
        std::string comparator;
        if (!find_top_level_comparison(expression, &compare_position, &comparator)) {
            continue;
        }

        const std::string lhs = std::string(
            refii::lua::TrimLuaWhitespace(expression.substr(0, compare_position)));
        const std::string rhs = std::string(refii::lua::TrimLuaWhitespace(
            expression.substr(compare_position + comparator.size())));
        if (lhs.empty() || rhs.empty() || lhs == rhs ||
            !is_safe_member_chain(lhs) || !is_safe_member_chain(rhs) ||
            !is_self_receiver(rhs) || is_self_receiver(lhs)) {
            continue;
        }

        const std::string rewritten =
            indent + "return " + rhs + swap_comparator(comparator) + lhs;
        if (rewritten == line) {
            continue;
        }
        line = rewritten;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaForwardFieldWritesBeforeLocalTableSeeds(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *is_local_out = false;
            lhs_out->clear();
            rhs_out->clear();

            bool parsed_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &parsed_is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = parsed_is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty() && !rhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }
            std::string normalized_lhs =
                std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            bool normalized_is_local = false;
            if (normalized_lhs.starts_with("local ")) {
                normalized_is_local = true;
                normalized_lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(normalized_lhs).substr(6)));
            }
            *is_local_out = normalized_is_local;
            *lhs_out = std::move(normalized_lhs);
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(parsed_rhs));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto extract_root_identifier =
        [](std::string_view lhs, std::string* root_out) -> bool {
            if (root_out == nullptr) {
                return false;
            }
            root_out->clear();

            lhs = refii::lua::TrimLuaWhitespace(lhs);
            if (lhs.empty()) {
                return false;
            }

            std::size_t split = lhs.find_first_of(".[");
            if (split == std::string_view::npos || split == 0) {
                return false;
            }

            const std::string root =
                std::string(refii::lua::TrimLuaWhitespace(lhs.substr(0, split)));
            if (!refii::lua::IsValidLuaIdentifier(root)) {
                return false;
            }
            *root_out = root;
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!parse_single_assignment((*lines)[line_index], &is_local, &lhs, &rhs) ||
                is_local || lhs.find('.') == std::string::npos ||
                lhs.find('[') != std::string::npos) {
                continue;
            }

            std::string root_identifier;
            if (!extract_root_identifier(lhs, &root_identifier) ||
                !refii::lua::IsRenderTemporaryName(root_identifier)) {
                continue;
            }

            std::size_t previous_non_empty = line_index;
            while (previous_non_empty > 0) {
                --previous_non_empty;
                const std::string_view previous_trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[previous_non_empty]));
                if (previous_trimmed.empty()) {
                    continue;
                }
                if (previous_trimmed != "end") {
                    previous_non_empty = lines->size();
                }
                break;
            }
            if (previous_non_empty == lines->size() ||
                previous_non_empty == line_index) {
                continue;
            }

            std::size_t assignment_end = line_index;
            if (rhs == "{") {
                const std::size_t table_literal_end =
                    refii::lua::FindRenderedTableLiteralEnd(*lines, line_index);
                if (table_literal_end >= lines->size() || table_literal_end < line_index) {
                    continue;
                }
                assignment_end = table_literal_end;
            }

            std::size_t local_seed_line = lines->size();
            std::string local_seed_rhs;
            const std::size_t lookahead_limit = std::min(lines->size(), assignment_end + 32);
            for (std::size_t probe = assignment_end + 1; probe < lookahead_limit; ++probe) {
                const std::string_view trimmed_probe =
                    refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed_probe.empty()) {
                    continue;
                }
                if (refii::lua::IsRenderedFunctionStartLine(trimmed_probe)) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (parse_single_assignment((*lines)[probe],
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs) &&
                    probe_is_local && probe_lhs == root_identifier &&
                    !probe_rhs.empty() && probe_rhs.front() == '{') {
                    local_seed_line = probe;
                    local_seed_rhs = std::move(probe_rhs);
                    break;
                }
            }
            if (local_seed_line >= lines->size()) {
                continue;
            }

            bool unsafe_between = false;
            for (std::size_t probe = assignment_end + 1; probe < local_seed_line; ++probe) {
                const std::string_view stripped =
                    refii::lua::StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    continue;
                }
                if (refii::lua::CountIdentifierUses(stripped, root_identifier) != 0) {
                    unsafe_between = true;
                    break;
                }
            }
            if (unsafe_between) {
                continue;
            }

            std::vector<std::string> moved_block;
            moved_block.reserve(assignment_end - line_index + 1);
            for (std::size_t move_index = line_index;
                 move_index <= assignment_end;
                 ++move_index) {
                moved_block.push_back(std::move((*lines)[move_index]));
            }
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                        lines->begin() + static_cast<std::ptrdiff_t>(assignment_end + 1));

            std::size_t adjusted_local_seed_line = local_seed_line;
            if (adjusted_local_seed_line > assignment_end) {
                adjusted_local_seed_line -= moved_block.size();
            } else {
                continue;
            }

            std::size_t insert_after = adjusted_local_seed_line;
            if (local_seed_rhs == "{") {
                const std::size_t literal_end =
                    refii::lua::FindRenderedTableLiteralEnd(*lines, adjusted_local_seed_line);
                if (literal_end < lines->size() && literal_end >= adjusted_local_seed_line) {
                    insert_after = literal_end;
                }
            }

            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(insert_after + 1),
                          std::make_move_iterator(moved_block.begin()),
                          std::make_move_iterator(moved_block.end()));
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaMissingIndexedTableSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *is_local_out = false;
            lhs_out->clear();
            rhs_out->clear();

            bool parsed_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &parsed_is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = parsed_is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty();
            }

            const std::string_view stripped = refii::lua::StripLuaLineComment(line);
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
            if (trimmed.empty() || trimmed.front() == '[' || trimmed.back() == ',') {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t equals_pos = std::string_view::npos;
            for (std::size_t cursor = 0; cursor < trimmed.size(); ++cursor) {
                const char ch = trimmed[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < trimmed.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < trimmed.size()) {
                        ++cursor;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    paren_depth = std::max(0, paren_depth - 1);
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    brace_depth = std::max(0, brace_depth - 1);
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    bracket_depth = std::max(0, bracket_depth - 1);
                    continue;
                }
                if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 &&
                    ch == '=') {
                    if (cursor + 1 < trimmed.size() && trimmed[cursor + 1] == '=') {
                        continue;
                    }
                    equals_pos = cursor;
                    break;
                }
            }
            if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                equals_pos + 1 >= trimmed.size()) {
                return false;
            }

            std::string lhs = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
            const std::string rhs = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
            if (lhs.empty() || rhs.empty()) {
                return false;
            }

            bool is_local = false;
            if (lhs.starts_with("local ")) {
                is_local = true;
                lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs).substr(6)));
            }
            if (lhs.empty()) {
                return false;
            }

            *is_local_out = is_local;
            *lhs_out = std::move(lhs);
            *rhs_out = rhs;
            return true;
        };

    const auto parse_index_base =
        [](std::string_view lhs, std::string* base_out) -> bool {
            if (base_out == nullptr) {
                return false;
            }
            base_out->clear();

            lhs = refii::lua::TrimLuaWhitespace(lhs);
            if (lhs.empty() || lhs.back() != ']') {
                return false;
            }

            std::size_t open_bracket = std::string_view::npos;
            int bracket_depth = 0;
            bool in_single_quote = false;
            bool in_double_quote = false;
            for (std::size_t cursor = lhs.size(); cursor-- > 0;) {
                const char ch = lhs[cursor];
                if (in_single_quote) {
                    if (ch == '\'' && (cursor == 0 || lhs[cursor - 1] != '\\')) {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '"' && (cursor == 0 || lhs[cursor - 1] != '\\')) {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == ']') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == '[') {
                    --bracket_depth;
                    if (bracket_depth == 0) {
                        open_bracket = cursor;
                        break;
                    }
                }
            }

            if (open_bracket == std::string_view::npos || open_bracket == 0) {
                return false;
            }

            const std::string base = std::string(
                refii::lua::TrimLuaWhitespace(lhs.substr(0, open_bracket)));
            if (base.empty() || base.find('(') != std::string::npos ||
                base.find(')') != std::string::npos) {
                return false;
            }

            *base_out = base;
            return true;
        };

    const auto is_simple_identifier =
        [](std::string_view expression) {
            return refii::lua::IsValidLuaIdentifier(refii::lua::TrimLuaWhitespace(expression));
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!parse_single_assignment((*lines)[line_index], &is_local, &lhs, &rhs) ||
                is_local || lhs.find('[') == std::string::npos) {
                continue;
            }

            std::string base_expression;
            if (!parse_index_base(lhs, &base_expression)) {
                continue;
            }
            if (!refii::lua::IsRenderTemporaryName(base_expression) &&
                base_expression.find('.') == std::string::npos) {
                continue;
            }

            bool already_seeded = false;
            std::size_t scanned_non_empty = 0;
            for (std::size_t probe = line_index; probe > 0 && scanned_non_empty < 256;) {
                --probe;
                const std::string_view stripped =
                    refii::lua::StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    continue;
                }
                ++scanned_non_empty;
                if (trimmed == "end") {
                    break;
                }
                if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (!parse_single_assignment((*lines)[probe],
                                             &probe_is_local,
                                             &probe_lhs,
                                             &probe_rhs)) {
                    continue;
                }
                if (probe_lhs == base_expression) {
                    already_seeded = true;
                    break;
                }
            }
            if (already_seeded) {
                continue;
            }

            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            const bool local_seed =
                is_simple_identifier(base_expression) &&
                refii::lua::IsRenderTemporaryName(base_expression);
            const std::string seed_line = indent +
                                          (local_seed ? "local " : std::string()) +
                                          base_expression + " = {}";
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          seed_line);
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaMissingFieldBaseSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *is_local_out = false;
            lhs_out->clear();
            rhs_out->clear();

            bool parsed_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &parsed_is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = parsed_is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }

            std::string lhs = std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            bool is_local = false;
            if (lhs.starts_with("local ")) {
                is_local = true;
                lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs).substr(6)));
            }
            if (lhs.empty()) {
                return false;
            }

            *is_local_out = is_local;
            *lhs_out = std::move(lhs);
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(parsed_rhs));
            return true;
        };

    const auto extract_root_identifier =
        [](std::string_view lhs, std::string* root_out) -> bool {
            if (root_out == nullptr) {
                return false;
            }
            root_out->clear();

            lhs = refii::lua::TrimLuaWhitespace(lhs);
            if (lhs.empty() || lhs.find('[') != std::string_view::npos) {
                return false;
            }

            const std::size_t dot = lhs.find('.');
            if (dot == std::string_view::npos || dot == 0) {
                return false;
            }

            const std::string root =
                std::string(refii::lua::TrimLuaWhitespace(lhs.substr(0, dot)));
            if (root.empty() || !refii::lua::IsRenderTemporaryName(root)) {
                return false;
            }
            *root_out = root;
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!parse_single_assignment((*lines)[line_index], &is_local, &lhs, &rhs) ||
                is_local) {
                continue;
            }

            std::string base_identifier;
            if (!extract_root_identifier(lhs, &base_identifier)) {
                continue;
            }

            bool already_seeded = false;
            std::size_t scanned_non_empty = 0;
            for (std::size_t probe = line_index; probe > 0 && scanned_non_empty < 256;) {
                --probe;
                const std::string_view stripped =
                    refii::lua::StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    continue;
                }
                ++scanned_non_empty;
                if (trimmed == "end" || refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (!parse_single_assignment((*lines)[probe],
                                             &probe_is_local,
                                             &probe_lhs,
                                             &probe_rhs)) {
                    continue;
                }
                if (probe_lhs == base_identifier) {
                    already_seeded = true;
                    break;
                }
            }
            if (already_seeded) {
                continue;
            }

            bool has_nearby_local_seed = false;
            std::size_t lookahead_non_empty = 0;
            for (std::size_t probe = line_index + 1;
                 probe < lines->size() && lookahead_non_empty < 24;
                 ++probe) {
                const std::string_view trimmed_probe = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed_probe.empty()) {
                    continue;
                }
                ++lookahead_non_empty;
                if (refii::lua::IsRenderedFunctionStartLine(trimmed_probe)) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (parse_single_assignment((*lines)[probe],
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs) &&
                    probe_is_local && probe_lhs == base_identifier &&
                    !probe_rhs.empty() && probe_rhs.front() == '{') {
                    has_nearby_local_seed = true;
                    break;
                }
            }
            if (has_nearby_local_seed) {
                continue;
            }

            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          indent + "local " + base_identifier + " = {}");
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

#include "decompiler/rendered/pipelines/rendered_seed_normalization_bundle.inc"

#include "decompiler/rendered/pipelines/rendered_instruction_microdiff_bundle.inc"

std::string RewriteLuaIdentifiersWithMap(
    std::string_view line,
    const std::unordered_map<std::string, std::string>& replacements);

#include "decompiler/rendered/pipelines/rendered_local_diff_exactifier.inc"

bool IsParityResultImproved(const refii::lua::audit::LuaBytecodeParityResult& candidate,
                            const refii::lua::audit::LuaBytecodeParityResult& baseline);

bool NormalizeRenderedLuaCleanupAnimationsReplayElseForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto trim_code_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto next_nonempty = [&](std::size_t start) {
        std::size_t index = start;
        while (index < lines->size() && trim_code_line((*lines)[index]).empty()) {
            ++index;
        }
        return index;
    };
    const auto find_if_end_without_else = [&](std::size_t start, std::size_t* end_out) {
        if (end_out == nullptr || start >= lines->size()) {
            return false;
        }
        const std::string_view header = trim_code_line((*lines)[start]);
        if (!header.starts_with("if ") || !header.ends_with(" then")) {
            return false;
        }

        int depth = 0;
        for (std::size_t index = start; index < lines->size(); ++index) {
            const std::string_view trimmed = trim_code_line((*lines)[index]);
            if (trimmed.empty()) {
                continue;
            }
            if (index > start && depth == 1 &&
                (trimmed == "else" || trimmed.starts_with("elseif "))) {
                return false;
            }
            depth += refii::lua::GetRenderedLuaBlockDelta(trimmed);
            if (index > start && depth == 0) {
                *end_out = index;
                return true;
            }
        }
        return false;
    };

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::size_t first_end = lines->size();
        if (!find_if_end_without_else(line_index, &first_end)) {
            continue;
        }

        bool has_cleanup_call = false;
        for (std::size_t probe = line_index + 1; probe < first_end; ++probe) {
            const std::string_view trimmed = trim_code_line((*lines)[probe]);
            if (trimmed.find(":PlayCleanupAnimations(") != std::string_view::npos) {
                has_cleanup_call = true;
                break;
            }
        }
        if (!has_cleanup_call) {
            continue;
        }

        const std::size_t replay_if = next_nonempty(first_end + 1);
        if (replay_if >= lines->size() ||
            trim_code_line((*lines)[replay_if]) != "if self.CleanupAnimations then") {
            continue;
        }

        std::size_t replay_end = lines->size();
        if (!find_if_end_without_else(replay_if, &replay_end)) {
            continue;
        }
        const std::size_t trailing_nil = next_nonempty(replay_end + 1);
        if (trailing_nil >= lines->size() ||
            trim_code_line((*lines)[trailing_nil]) != "self.CleanupAnimations = nil") {
            continue;
        }

        (*lines)[first_end] = GetLuaLineIndent((*lines)[line_index]) + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(trailing_nil + 1),
                      GetLuaLineIndent((*lines)[line_index]) + "end");
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaTerminalReturnOverrideForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto trim_code_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto next_nonempty = [&](std::size_t start) {
        std::size_t index = start;
        while (index < lines->size() && trim_code_line((*lines)[index]).empty()) {
            ++index;
        }
        return index;
    };
    const auto is_if_header = [&](std::string_view line) {
        const std::string_view trimmed = trim_code_line(line);
        return trimmed.starts_with("if ") && trimmed.ends_with(" then");
    };
    const auto find_simple_if_end = [&](std::size_t start,
                                        std::size_t* return_line_out,
                                        std::size_t* end_line_out) {
        if (return_line_out == nullptr || end_line_out == nullptr ||
            start >= lines->size() || !is_if_header((*lines)[start])) {
            return false;
        }

        *return_line_out = lines->size();
        *end_line_out = lines->size();
        int depth = 0;
        std::size_t top_level_body_lines = 0;
        for (std::size_t index = start; index < lines->size(); ++index) {
            const std::string_view trimmed = trim_code_line((*lines)[index]);
            if (trimmed.empty()) {
                continue;
            }
            if (index > start && depth == 1 &&
                (trimmed == "else" || trimmed.starts_with("elseif "))) {
                return false;
            }
            if (index > start && depth == 1 && trimmed != "end") {
                ++top_level_body_lines;
                if (!trimmed.starts_with("return ")) {
                    return false;
                }
                *return_line_out = index;
            }

            depth += refii::lua::GetRenderedLuaBlockDelta(trimmed);
            if (index > start && depth == 0) {
                *end_line_out = index;
                return top_level_body_lines == 1 && *return_line_out < *end_line_out;
            }
        }
        return false;
    };

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::size_t return_line = lines->size();
        std::size_t end_line = lines->size();
        if (!find_simple_if_end(line_index, &return_line, &end_line)) {
            continue;
        }

        const std::size_t trailing_return_line = next_nonempty(end_line + 1);
        if (trailing_return_line >= lines->size()) {
            continue;
        }
        const std::string_view trailing_return = trim_code_line((*lines)[trailing_return_line]);
        if (!trailing_return.starts_with("return ")) {
            continue;
        }

        const std::string target_name = std::string(refii::lua::TrimLuaWhitespace(
            trailing_return.substr(std::string_view("return ").size())));
        if (!refii::lua::IsValidLuaIdentifier(target_name) ||
            refii::lua::IsLuaKeyword(target_name)) {
            continue;
        }

        const std::string_view override_return = trim_code_line((*lines)[return_line]);
        const std::string override_expression = std::string(refii::lua::TrimLuaWhitespace(
            override_return.substr(std::string_view("return ").size())));
        if (override_expression.empty() || override_expression == target_name ||
            override_expression.find(',') != std::string::npos) {
            continue;
        }

        (*lines)[return_line] =
            GetLuaLineIndent((*lines)[return_line]) + target_name + " = " + override_expression;
        return true;
    }

    return false;
}

bool TryApplyTerminalEmptyElseIfsForParity(
    const refii::lua::audit::LuaBytecodeParityReference& parity_reference,
    std::string_view chunk_name,
    std::string* source,
    std::string* note,
    std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext base_context(*source);
    const std::vector<std::string>& base_lines = base_context.EnsureLines();
    if (base_lines.empty()) {
        return false;
    }

    const auto trim_code_line = [](std::string_view line) {
        return refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto is_if_header = [&](std::string_view line) {
        const std::string_view trimmed = trim_code_line(line);
        return trimmed.starts_with("if ") && trimmed.ends_with(" then");
    };
    const auto next_nonempty = [&](const std::vector<std::string>& lines,
                                   std::size_t start) {
        std::size_t index = start;
        while (index < lines.size() && trim_code_line(lines[index]).empty()) {
            ++index;
        }
        return index;
    };
    const auto find_if_end_without_else = [&](const std::vector<std::string>& lines,
                                              std::size_t start,
                                              std::size_t* end_out) {
        if (end_out == nullptr || start >= lines.size() || !is_if_header(lines[start])) {
            return false;
        }

        int depth = 0;
        std::size_t significant_body_lines = 0;
        for (std::size_t index = start; index < lines.size(); ++index) {
            const std::string_view trimmed = trim_code_line(lines[index]);
            if (trimmed.empty()) {
                continue;
            }
            if (index > start && depth == 1 &&
                (trimmed == "else" || trimmed.starts_with("elseif "))) {
                return false;
            }
            if (index > start && depth == 1 && trimmed != "end") {
                ++significant_body_lines;
                if (significant_body_lines > 4 || trimmed == "return" ||
                    trimmed.starts_with("return ") || trimmed == "break") {
                    return false;
                }
            }

            depth += refii::lua::GetRenderedLuaBlockDelta(trimmed);
            if (index > start && depth == 0) {
                *end_out = index;
                return significant_body_lines > 0;
            }
        }
        return false;
    };

    const auto join_lines = [](const std::vector<std::string>& lines) {
        std::string joined;
        for (std::size_t index = 0; index < lines.size(); ++index) {
            if (index != 0) {
                joined.push_back('\n');
            }
            joined += lines[index];
        }
        return joined;
    };

    for (std::size_t line_index = 0; line_index < base_lines.size(); ++line_index) {
        std::size_t if_end = base_lines.size();
        if (!find_if_end_without_else(base_lines, line_index, &if_end)) {
            continue;
        }

        const std::size_t following = next_nonempty(base_lines, if_end + 1);
        if (following >= base_lines.size() || trim_code_line(base_lines[following]) != "end") {
            continue;
        }

        std::vector<std::string> candidate_lines = base_lines;
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(if_end),
                               GetLuaLineIndent(candidate_lines[line_index]) + "else");
        std::string candidate_source = join_lines(candidate_lines);
        if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
            continue;
        }

        refii::lua::audit::LuaBytecodeParityResult parity_result;
        if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                                candidate_source,
                                                                chunk_name,
                                                                &parity_result) ||
            !parity_result.exact_bytecode) {
            continue;
        }

        *source = std::move(candidate_source);
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_exact_parity_normalized_terminal_empty_else");
        }
        return true;
    }

    return false;
}

bool TryApplyRenderedExactParityNormalizationCandidate(
    const refii::lua::audit::LuaBytecodeParityReference& parity_reference,
    std::string_view chunk_name,
    std::string* source,
    std::string* note,
    std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    refii::lua::audit::LuaBytecodeParityResult baseline_parity;
    if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                           *source,
                                                           chunk_name,
                                                           &baseline_parity) &&
        TryExactifyLocalDiffHeaderParameters(
            parity_reference,
            chunk_name,
            source,
            &baseline_parity,
            note,
            note_prefix)) {
        return true;
    }
    if (TryApplyTerminalEmptyElseIfsForParity(
            parity_reference, chunk_name, source, note, note_prefix)) {
        return true;
    }

    const auto try_variant = [&](const auto& transform,
                                 std::string_view suffix) -> bool {
        refii::lua::internal::LuaMutableTextContext candidate_context(*source);
        std::vector<std::string>* lines = candidate_context.EnsureMutableLines();
        if (!transform(lines)) {
            return false;
        }
        candidate_context.MarkLinesMutated();

        std::string candidate_source = std::string(candidate_context.EnsureSource());
        if (!candidate_context.ValidateCached(chunk_name).empty()) {
            return false;
        }

        refii::lua::audit::LuaBytecodeParityResult parity_result;
        if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                                candidate_source,
                                                                chunk_name,
                                                                &parity_result) ||
            !parity_result.exact_bytecode) {
            return false;
        }

        *source = std::move(candidate_source);
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_exact_parity_normalized" +
                                  (suffix.empty() ? std::string() : "_" + std::string(suffix)));
        }
        return true;
    };

    struct ExactParityVariantStep {
        bool (*transform)(std::vector<std::string>* lines);
        std::string_view suffix;
    };

    static const ExactParityVariantStep kVariantSteps[] = {
        {NormalizeRenderedLuaTerminalReturnOverrideForParity,
         "terminal_return_override"},
        {NormalizeRenderedLuaCleanupAnimationsReplayElseForParity,
         "cleanup_animations_replay_else"},
        {refii::lua::ExpandRenderedLuaTerminalEmptyElseIfChainsForParity,
         "terminal_empty_elseif"},
        {refii::lua::ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity,
         "inline_top_level_table_seed_fields"},
        {refii::lua::ExpandRenderedLuaOrTableFallbackAssignmentsForParity,
         "or_table_fallback_assignments"},
        {refii::lua::ExpandRenderedLuaInlineLocalTableSeedFieldsForParity,
         "inline_local_table_seed_fields"},
        {TryApplyRenderedLuaSeedReturnDebugMicroDiffBundle,
         "seed_return_debug_microdiff_bundle"},
        {refii::lua::RestoreRenderedLuaTopLevelSubclassBindings, "subclass_bindings"},
        {refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries,
         "scoped_sequential_member_carries"},
        {refii::lua::SimplifyRenderedLuaScopedMemberAliases, "scoped_member_aliases"},
        {refii::lua::SimplifyRenderedLuaTopLevelMemberAliases, "top_level_member_aliases"},
        {refii::lua::SimplifyRenderedLuaDeadTemporaryAliasAssignments,
         "dead_temporary_alias_assignments"},
        {refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments,
         "immediate_temporary_assignments"},
        {refii::lua::SimplifyRenderedLuaImmediateTemporaryCallArgumentUses,
         "immediate_temporary_call_argument_uses"},
        {refii::lua::SimplifyRenderedLuaImmediateTemporaryStatementUses,
         "immediate_temporary_statement_uses"},
        {refii::lua::SimplifyRenderedLuaConditionHeaders, "condition_headers"},
        {refii::lua::SimplifyRenderedLuaRedundantGuardConditionParentheses,
         "redundant_guard_condition_parentheses"},
        {refii::lua::HoistRenderedLuaSingleWhileElseTailsForExactParity,
         "single_while_else_tails"},
        {refii::lua::CollapseRenderedLuaNestedSharedElseGuardsForExactParity,
         "nested_shared_else_guards"},
        {refii::lua::CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity,
         "reused_condition_temp_else_guards"},
        {refii::lua::RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
         "subsumed_tail_break_guards"},
        {refii::lua::SimplifyRenderedLuaAdjacentDuplicateCallValueConditions,
         "adjacent_duplicate_call_value_conditions"},
        {refii::lua::SimplifyRenderedLuaRepeatedCallGuards, "repeated_call_guards"},
        {refii::lua::SimplifyRenderedLuaRepeatedCallFieldGuardsForParity,
         "repeated_call_field_guards"},
        {refii::lua::NormalizeRenderedLuaMeterPercentLocalForParity,
         "meter_percent_local_global"},
        {refii::lua::NormalizeRenderedLuaDebugDrawTextZOffsetForParity,
         "debug_draw_text_z_offset"},
        {refii::lua::SimplifyRenderedLuaScopedRepeatedCallComparisonGuardsForParity,
         "scoped_repeated_call_comparison_guards"},
        {refii::lua::NormalizeRenderedLuaTimingRelationalCallOrderForParity,
         "timing_relational_call_order"},
        {refii::lua::ExpandRenderedLuaAndNotElseGuardsForParity,
         "and_not_else_guards"},
        {refii::lua::SimplifyRenderedLuaImmediateAssignedConditionExpressionUses,
         "immediate_assigned_condition_expression_uses"},
        {refii::lua::SimplifyRenderedLuaRepeatedLocalCallPropertyUsesForParity,
         "repeated_local_call_property_uses"},
        {refii::lua::SimplifyRenderedLuaRepeatedLocalCallExpressionUsesForParity,
         "repeated_local_call_expression_uses"},
        {refii::lua::SimplifyRenderedLuaRepeatedLocalExpressionUsesForParity,
         "repeated_local_expression_uses"},
        {refii::lua::SimplifyRenderedLuaRepeatedMethodExpressionConditionsForParity,
         "repeated_method_expression_conditions"},
        {refii::lua::ExpandRenderedLuaPostCallLocalAliasesForParity,
         "post_call_local_aliases"},
        {refii::lua::LocalizeRenderedLuaGroupMindAttachedGuardCallsForParity,
         "groupmind_attached_guard_call_locals"},
        {refii::lua::SimplifyRenderedLuaTopLevelImmediateResultAliases,
         "top_level_immediate_result_aliases"},
        {refii::lua::SimplifyRenderedLuaLocalTableBuilderCallAliases,
         "local_table_builder_call_aliases"},
        {refii::lua::SimplifyRenderedLuaTopLevelFunctionAliasAssignments,
         "top_level_function_aliases"},
        {refii::lua::SimplifyRenderedLuaTopLevelTableLiteralAliasBuilders,
         "top_level_table_literal_aliases"},
        {refii::lua::SimplifyRenderedLuaDanglingTopLevelIndexedAliases,
         "dangling_top_level_indexed_aliases"},
        {refii::lua::SimplifyRenderedLuaTableLiteralCapturedTargetAliases,
         "table_literal_captured_target_aliases"},
        {refii::lua::NormalizeRenderedLuaFreshTargetTableSeeds, "fresh_target_table_seeds"},
        {refii::lua::NormalizeRenderedLuaStaleTargetTableAliases, "stale_target_table_aliases"},
        {refii::lua::SimplifyRenderedLuaStaleTargetLiteralOverwrites,
         "stale_target_literal_overwrites"},
        {refii::lua::SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments,
         "top_level_multi_table_assignments"},
        {refii::lua::SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments,
         "setlist_collapsed_top_level_assignments"},
        {refii::lua::SimplifyRenderedLuaTopLevelObjectCarryAssignments,
         "top_level_object_carries"},
        {refii::lua::SimplifyRenderedLuaKnownFieldCarryAssignments, "known_field_carries"},
        {refii::lua::SimplifyRenderedLuaImmediateLocalLiteralSeeds,
         "immediate_local_literal_seeds"},
        {refii::lua::SimplifyRenderedLuaForwardLocalLiteralSeeds,
         "forward_local_literal_seeds"},
        {refii::lua::SimplifyRenderedLuaTableBuilders, "table_builders"},
        {refii::lua::SimplifyRenderedLuaLocalTableBuilderCallArguments,
         "local_table_builder_call_arguments"},
        {refii::lua::SimplifyRenderedLuaTableInitializerTemporaries,
         "table_initializer_temporaries"},
        {refii::lua::SimplifyRenderedLuaTableLiteralTemporaries, "table_literal_temporaries"},
        {refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals,
         "immediate_identifier_carry_locals"},
        {refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals,
         "immediate_expression_carry_locals"},
        {refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals,
         "immediate_member_carry_locals"},
        {refii::lua::SimplifyRenderedLuaClosureShadowLocalAssignments,
         "closure_shadow_local_assignments"},
        {refii::lua::SimplifyRenderedLuaImmediateLocalCallFieldConditions,
         "immediate_local_call_field_conditions"},
        {refii::lua::SimplifyRenderedLuaUnusedLocalTableLiteralAssignmentsForParity,
         "unused_local_table_literal_assignments"},
        {refii::lua::SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals,
         "immediate_iterator_argument_carry_locals"},
        {refii::lua::SimplifyRenderedLuaLoopTrailingReturnElse, "loop_trailing_return_else"},
        {refii::lua::ExpandRenderedLuaReturnThenFallbackElseForParity,
         "return_then_fallback_else"},
        {refii::lua::ExpandRenderedLuaTerminalIfActionEmptyElseForParity,
         "terminal_if_action_empty_else"},
        {refii::lua::CollapseRenderedLuaNestedDuplicateReturnGuardsForParity,
         "nested_duplicate_return_guards"},
        {refii::lua::SimplifyRenderedLuaDuplicateIfElseReturnsForParity,
         "duplicate_if_else_returns"},
        {refii::lua::RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
         "subsumed_tail_break_guards"},
        {RestoreRenderedLuaEmptyEnumTextElseIfAssignmentsForParity,
         "empty_enum_text_elseif_assignments"},
        {NormalizeRenderedLuaBranchReturnIntoFinalReturnAssignmentForParity,
         "branch_return_final_assignment"},
        {NormalizeRenderedLuaRepeatedMultiReturnPredicateForParity,
         "repeated_multi_return_predicate"},
        {NormalizeRenderedLuaNegativeOrPredicateReturnsForParity,
         "negative_or_predicate_returns"},
        {refii::lua::ComposeRenderedLuaPredicateReturnParityCleanupTail,
         "predicate_return_parity_tail"},
        {refii::lua::ExpandRenderedLuaPredicateBooleanReturnsForParity,
         "predicate_boolean_returns"},
        {refii::lua::ExpandRenderedLuaNegatedBooleanReturnsForParity,
         "negated_boolean_returns"},
        {refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
         "redundant_colon_self_arguments"},
        {refii::lua::SimplifyRenderedLuaExplicitSelfMethodArguments,
         "explicit_self_method_arguments"},
        {refii::lua::SimplifyRenderedLuaMethodArg0SelfParameters,
         "method_arg0_self_parameters"},
        {refii::lua::UnwrapRenderedLuaReturnedCallsForExactParity, "returned_calls_unwrapped"},
        {refii::lua::WrapRenderedLuaReturnedCallsForExactParity, "returned_calls_wrapped"},
        {refii::lua::TrySwapRenderedLeadingCallComparisonsForExactParity,
         "leading_call_comparisons"},
        {refii::lua::TrySwapRenderedStableRelationalComparisonsForParity,
         "stable_relational_comparisons"},
        {refii::lua::NormalizeRenderedLuaLoopBreakElseToPostIfForParity,
         "loop_break_else_post_if"},
        {refii::lua::NormalizeRenderedLuaPostGuardDebugErrorElseForParity,
         "post_guard_debug_error_else"},
        {refii::lua::TrySwapRenderedCallRightHandComparisonsForParity,
         "header_comparisons"},
        {TryApplyRenderedLuaNearExactInstructionMicroDiffCleanup,
         "near_exact_instruction_microdiff_cleanup"},
        {TryApplyRenderedLuaNearExactLocalTimingCleanup, "near_exact_local_timing_cleanup"},
        {refii::lua::ComposeRenderedLuaOrderedCompareSelfCleanupTail,
         "ordered_compare_self_cleanup_tail"},
        {refii::lua::ComposeRenderedLuaNamespaceExportAliasCleanupTail,
         "namespace_export_alias_cleanup_tail"},
        {refii::lua::ComposeRenderedLuaTopLevelAliasCleanupTail,
         "top_level_alias_cleanup_tail"},
        {refii::lua::ComposeRenderedLuaMemberAliasCleanupTail,
         "member_alias_cleanup_tail"},
        {refii::lua::ComposeRenderedLuaSmallCallCleanupTail, "small_call_cleanup_tail"},
        {refii::lua::ComposeRenderedLuaAssignedGuardCleanupTail, "assigned_guard_cleanup_tail"},
        {refii::lua::ComposeRenderedLuaConditionTempGuardCleanupTailForNearExact,
         "condition_temp_guard_cleanup_tail"},
        {refii::lua::ComposeRenderedLuaNearExactCommitParityCleanupTail,
         "near_exact_commit_parity_tail"},
        {refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps,
         "straight_line_single_use_temps"},
        {TryApplyValidatedOutputCleanupProfile, "validated_cleanup"},
        {refii::lua::ApplyRenderedLuaExactParityNormalizationProfile, ""},
    };

    for (const ExactParityVariantStep& step : kVariantSteps) {
        if (try_variant(step.transform, step.suffix)) {
            return true;
        }
    }
    return false;
}

bool TryApplyRenderedParityImprovingNormalizationCandidate(
    const refii::lua::audit::LuaBytecodeParityReference& parity_reference,
    std::string_view chunk_name,
    std::string* source,
    refii::lua::audit::LuaBytecodeParityResult* parity_state,
    std::string* note,
    std::string_view note_prefix) {
    if (source == nullptr || source->empty() || parity_state == nullptr) {
        return false;
    }

    refii::lua::audit::LuaBytecodeParityResult baseline_parity = *parity_state;
    if (!baseline_parity.first_mismatch.available && !baseline_parity.exact_bytecode &&
        !refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                            *source,
                                                            chunk_name,
                                                            &baseline_parity)) {
        return false;
    }

    const auto try_variant = [&](bool (*transform)(std::vector<std::string>*),
                                 std::string_view suffix) -> bool {
        refii::lua::internal::LuaMutableTextContext candidate_context(*source);
        std::vector<std::string>* lines = candidate_context.EnsureMutableLines();
        if (!transform(lines)) {
            return false;
        }
        candidate_context.MarkLinesMutated();
        if (!candidate_context.ValidateCached(chunk_name).empty()) {
            return false;
        }

        std::string candidate_source = std::string(candidate_context.EnsureSource());
        refii::lua::audit::LuaBytecodeParityResult candidate_parity;
        if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                                candidate_source,
                                                                chunk_name,
                                                                &candidate_parity)) {
            return false;
        }
        if (!candidate_parity.exact_bytecode &&
            !IsParityResultImproved(candidate_parity, baseline_parity)) {
            return false;
        }

        *source = std::move(candidate_source);
        *parity_state = std::move(candidate_parity);
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_parity_improving_normalized" +
                                  (suffix.empty() ? std::string() : "_" + std::string(suffix)));
        }
        return true;
    };

    bool changed = false;
    if (try_variant(NormalizeRenderedLuaTerminalReturnOverrideForParity,
                    "terminal_return_override")) {
        baseline_parity = *parity_state;
        changed = true;
    }
    if (try_variant(NormalizeRenderedLuaCleanupAnimationsReplayElseForParity,
                    "cleanup_animations_replay_else")) {
        baseline_parity = *parity_state;
        changed = true;
    }
    for (int pass = 0; pass < 4 && !baseline_parity.exact_bytecode; ++pass) {
        bool pass_changed = false;

        if (try_variant(refii::lua::TrySwapRenderedLeadingCallComparisonsForExactParity,
                        "leading_call_comparisons")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::ExpandRenderedLuaNegatedBooleanReturnsForParity,
                        "negated_boolean_returns")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }

        const auto mismatch_note_contains = [&](std::string_view needle) -> bool {
            return baseline_parity.first_mismatch.available &&
                   baseline_parity.first_mismatch.note.find(needle) != std::string::npos;
        };
        const bool wants_wrapped_return_call =
            mismatch_note_contains("CALL(") && mismatch_note_contains("-> TAILCALL(");
        const bool wants_unwrapped_return_call =
            mismatch_note_contains("TAILCALL(") && mismatch_note_contains("-> CALL(");
        if (!baseline_parity.exact_bytecode && wants_wrapped_return_call &&
            try_variant(refii::lua::WrapRenderedLuaReturnedCallsForExactParity,
                        "returned_calls_wrapped")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode && wants_unwrapped_return_call &&
            try_variant(refii::lua::UnwrapRenderedLuaReturnedCallsForExactParity,
                        "returned_calls_unwrapped")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaImmediateLocalCallFieldConditions,
                        "immediate_local_call_field_conditions")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaUnusedLocalTableLiteralAssignmentsForParity,
                        "unused_local_table_literal_assignments")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity,
                        "inline_top_level_table_seed_fields")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::ExpandRenderedLuaInlineLocalTableSeedFieldsForParity,
                        "inline_local_table_seed_fields")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaRepeatedCallFieldGuardsForParity,
                        "repeated_call_field_guards")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaRepeatedLocalCallPropertyUsesForParity,
                        "repeated_local_call_property_uses")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaRepeatedLocalCallExpressionUsesForParity,
                        "repeated_local_call_expression_uses")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaRepeatedLocalExpressionUsesForParity,
                        "repeated_local_expression_uses")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaRepeatedMethodExpressionConditionsForParity,
                        "repeated_method_expression_conditions")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::ExpandRenderedLuaPostCallLocalAliasesForParity,
                        "post_call_local_aliases")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::LocalizeRenderedLuaGroupMindAttachedGuardCallsForParity,
                        "groupmind_attached_guard_call_locals")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::NormalizeRenderedLuaMeterPercentLocalForParity,
                        "meter_percent_local_global")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::NormalizeRenderedLuaDebugDrawTextZOffsetForParity,
                        "debug_draw_text_z_offset")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaScopedRepeatedCallComparisonGuardsForParity,
                        "scoped_repeated_call_comparison_guards")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::NormalizeRenderedLuaTimingRelationalCallOrderForParity,
                        "timing_relational_call_order")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
                        "subsumed_tail_break_guards")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::ExpandRenderedLuaAndNotElseGuardsForParity,
                        "and_not_else_guards")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(RestoreRenderedLuaEmptyEnumTextElseIfAssignmentsForParity,
                        "empty_enum_text_elseif_assignments")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::ExpandRenderedLuaReturnThenFallbackElseForParity,
                        "return_then_fallback_else")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::ExpandRenderedLuaTerminalIfActionEmptyElseForParity,
                        "terminal_if_action_empty_else")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::CollapseRenderedLuaNestedDuplicateReturnGuardsForParity,
                        "nested_duplicate_return_guards")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaDuplicateIfElseReturnsForParity,
                        "duplicate_if_else_returns")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::SimplifyRenderedLuaClosureShadowLocalAssignments,
                        "closure_shadow_local_assignments")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::TrySwapRenderedStableRelationalComparisonsForParity,
                        "stable_relational_comparisons")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::NormalizeRenderedLuaLoopBreakElseToPostIfForParity,
                        "loop_break_else_post_if")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }
        if (!baseline_parity.exact_bytecode &&
            try_variant(refii::lua::NormalizeRenderedLuaPostGuardDebugErrorElseForParity,
                        "post_guard_debug_error_else")) {
            baseline_parity = *parity_state;
            changed = true;
            pass_changed = true;
        }

        if (!pass_changed) {
            break;
        }
    }
    return changed;
}

bool TryExtractFirstFunctionBlock(const std::vector<std::string>& lines,
                                  std::size_t* start_line_out,
                                  std::size_t* end_line_out);
bool TryRepairNativeCandidateSource(std::string& candidate_source,
                                    const std::vector<char>* input_bytes,
                                    std::string* note,
                                    std::string_view note_prefix,
                                    std::string* validation_error_out = nullptr);

bool TryLocalizeTopLevelTemporaryAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    std::vector<bool> inside_function(lines->size(), false);
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        const bool starts_function =
            StartsWith(trimmed, "function ") || StartsWith(trimmed, "function(") ||
            trimmed.find("= function") != std::string_view::npos;
        if (!starts_function) {
            continue;
        }

        const std::size_t function_end =
            refii::lua::FindRenderedFunctionBlockEnd(*lines, line_index);
        if (function_end >= lines->size() || function_end < line_index) {
            continue;
        }

        for (std::size_t i = line_index; i <= function_end; ++i) {
            inside_function[i] = true;
        }
        line_index = function_end;
    }

    std::unordered_set<std::string> localized_temporaries;
    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (inside_function[line_index]) {
            continue;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (trimmed.empty()) {
            continue;
        }

        std::string_view candidate = trimmed;
        if (StartsWith(candidate, "local ")) {
            candidate = TrimWhitespace(candidate.substr(6));
            const std::size_t end = candidate.find_first_of(" \t=,.[(:");
            const std::string_view name =
                end == std::string_view::npos ? candidate : candidate.substr(0, end);
            if (LooksLikeRegisterizedTemporaryName(name)) {
                localized_temporaries.emplace(name);
            }
            continue;
        }

        const std::size_t equal = candidate.find('=');
        if (equal == std::string_view::npos) {
            continue;
        }

        const std::string_view lhs = TrimWhitespace(candidate.substr(0, equal));
        if (!LooksLikeRegisterizedTemporaryName(lhs)) {
            continue;
        }
        if (localized_temporaries.contains(std::string(lhs))) {
            continue;
        }

        const std::size_t indent = (*lines)[line_index].find_first_not_of(" \t");
        if (indent == std::string::npos) {
            continue;
        }

        (*lines)[line_index].insert(indent, "local ");
        localized_temporaries.emplace(lhs);
        changed = true;
    }

    return changed;
}

std::size_t ExtractLuaOpenConstructLine(std::string_view validation_error);

struct DecompiledFunctionSpan {
    std::size_t start_line = 0;
    std::size_t end_line = 0;
    std::string prefix;
    std::string suffix;
    bool has_error = false;
    bool looks_suspicious = false;
    bool top_level = false;
};

bool IsTopLevelAnonymousBootstrapSpan(const DecompiledFunctionSpan& span) {
    if (!span.top_level) {
        return false;
    }

    const std::string_view trimmed_prefix = TrimWhitespace(span.prefix);
    return trimmed_prefix.empty() || trimmed_prefix == "(" || trimmed_prefix == ";(";
}

bool SpanUsesTemporaryParameterNamesWhenChunkHasNamedParameters(
    const std::vector<std::string>& lines,
    const DecompiledFunctionSpan& span,
    const refii::lua::Lua51Function& function) {
    if (span.start_line >= lines.size() || function.parameter_count == 0) {
        return false;
    }
    if (span.start_line >= span.end_line) {
        return false;
    }

    std::size_t scanned = 0;
    bool looks_like_closure_factory = false;
    for (std::size_t line_index = span.start_line + 1;
         line_index <= span.end_line && line_index < lines.size() && scanned < 4;
         ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(lines[line_index]));
        if (trimmed.empty()) {
            continue;
        }
        ++scanned;
        if (StartsWith(trimmed, "return function(") ||
            StartsWith(trimmed, "return function ")) {
            looks_like_closure_factory = true;
            break;
        }
        if (trimmed != "local" && !StartsWith(trimmed, "local ")) {
            break;
        }
    }
    if (!looks_like_closure_factory) {
        return false;
    }

    std::vector<std::string> rendered_parameters;
    if (!TryExtractRenderedFunctionHeaderParameters(lines[span.start_line],
                                                    &rendered_parameters) ||
        rendered_parameters.size() != function.parameter_count) {
        return false;
    }

    bool all_rendered_temporary = true;
    for (const std::string& parameter : rendered_parameters) {
        if (!refii::lua::IsRenderTemporaryName(parameter)) {
            all_rendered_temporary = false;
            break;
        }
    }
    if (!all_rendered_temporary) {
        return false;
    }

    const std::vector<std::string> chunk_parameters =
        CollectUsefulChunkParameterNames(function);
    if (chunk_parameters.size() != rendered_parameters.size()) {
        return false;
    }

    for (std::size_t index = 0; index < rendered_parameters.size(); ++index) {
        if (rendered_parameters[index] != chunk_parameters[index]) {
            return true;
        }
    }
    return false;
}

struct FunctionReplacement {
    std::size_t start_line = 0;
    std::size_t end_line = 0;
    std::size_t function_index = 0;
    bool top_level = false;
    bool looks_suspicious = false;
    bool has_error = false;
    bool exact_in_isolation = false;
    bool improved_in_isolation = false;
    int specificity_score = 0;
    std::vector<std::string> lines;
};

struct LuaReadabilitySummary {
    std::size_t nonempty_line_count = 0;
    std::size_t temporary_identifier_count = 0;
    std::size_t unique_temporary_identifier_count = 0;
    std::size_t temporary_leading_line_count = 0;
    std::size_t temporary_local_declaration_count = 0;
    std::size_t temporary_assignment_count = 0;
    std::size_t temporary_table_field_assignment_count = 0;
    std::size_t temporary_self_reference_assignment_count = 0;
    std::size_t multi_temporary_token_line_count = 0;
};

template <typename Callback>
void ForEachLuaToken(std::string_view line, Callback&& callback);

std::vector<DecompiledFunctionSpan> CollectDecompiledFunctionSpans(
    const std::vector<std::string>& lines);
bool TryInferClosureAssignmentPrefix(const refii::lua::Lua51Function& main_function,
                                     std::size_t function_index,
                                     std::string* prefix_out);
std::unordered_map<std::string, DecompiledFunctionSpan> BuildAssignedPrefixSpanMap(
    const std::vector<std::string>& lines,
    const std::vector<DecompiledFunctionSpan>& spans);
bool TryExtractAssignedPrefixFromSpanHeader(const std::vector<std::string>& lines,
                                            const DecompiledFunctionSpan& span,
                                            std::string* prefix_out);
bool TryExtractFunctionSpanByHeaderPrefix(const std::vector<std::string>& lines,
                                          std::string_view assigned_prefix,
                                          DecompiledFunctionSpan* span_out);
bool TryExtractTopLevelFunctionSpanByPrefixUntilNextBoundary(
    const std::vector<std::string>& lines,
    std::string_view assigned_prefix,
    DecompiledFunctionSpan* span_out);
bool TryFindFunctionSpanByTemporaryAssignmentPrefix(
    const std::vector<std::string>& lines,
    std::string_view assigned_prefix,
    DecompiledFunctionSpan* span_out);
bool TryFindFunctionSpanByPrefix(const std::vector<std::string>& lines,
                                 std::string_view assigned_prefix,
                                 DecompiledFunctionSpan* span_out);
bool TryResolveFunctionSpanForIndex(const refii::lua::Lua51Function& main_function,
                                    std::size_t preorder_function_index,
                                    std::size_t root_child_index,
                                    const std::vector<std::string>& lines,
                                    const std::vector<DecompiledFunctionSpan>& spans,
                                    const std::unordered_map<std::string, DecompiledFunctionSpan>*
                                        prefix_span_map,
                                    DecompiledFunctionSpan* span_out);
bool ContainsObviouslyInvalidNilUsage(std::string_view source);

#include "decompiler/rendered/pipelines/repair_state_keys.inc"

#include "decompiler/rendered/pipelines/repair_progress_helpers.inc"

#include "decompiler/rendered/pipelines/top_level_replacement_state_helpers.inc"

#include "decompiler/rendered/pipelines/output_placeholder_helpers.inc"

#include "decompiler/rendered/pipelines/rendered_readability_and_nil_cleanup.inc"

enum class LuaTextBlockKind {
    kFunction,
    kBlock,
    kRepeat,
};

struct LuaTextBlock {
    LuaTextBlockKind kind = LuaTextBlockKind::kBlock;
    std::size_t span_index = 0;
};

template <typename Callback>
void ForEachLuaToken(std::string_view line, Callback&& callback) {
    const refii::lua::internal::LuaTokenScanOptions scan_options{
        .mode = refii::lua::internal::LuaTokenScanMode::kCommentAware,
        .parse_quoted_literals = true,
        .use_stable_copy = false,
    };
    refii::lua::internal::ForEachLuaIdentifierToken(line, scan_options, callback);
}

bool HasInlineFunctionEnd(std::string_view line) {
    bool saw_function = false;
    bool saw_end_after_function = false;
    ForEachLuaToken(line, [&](std::string_view token, std::size_t, std::size_t) {
        if (token == "function") {
            saw_function = true;
            return;
        }
        if (saw_function && token == "end") {
            saw_end_after_function = true;
        }
    });
    return saw_function && saw_end_after_function;
}

bool LooksLikeBrokenMultiResultLocalSequence(const std::vector<std::string>& lines,
                                             std::size_t start_line,
                                             std::size_t end_line) {
    if (start_line >= lines.size() || end_line >= lines.size() || start_line >= end_line) {
        return false;
    }

    auto next_significant = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i <= end_line && i < lines.size(); ++i) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[i]));
            if (!trimmed.empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t i = start_line; i <= end_line && i < lines.size(); ++i) {
        const std::string_view first_trimmed = TrimWhitespace(StripLineComment(lines[i]));
        const std::string_view first_name = ExtractLocalDeclarationName(first_trimmed);
        if (first_name.empty() || first_trimmed.find('=') != std::string_view::npos) {
            continue;
        }

        const std::size_t second_index = next_significant(i + 1);
        if (second_index > end_line || second_index >= lines.size()) {
            continue;
        }

        const std::string_view second_trimmed =
            TrimWhitespace(StripLineComment(lines[second_index]));
        if (!StartsWith(second_trimmed, "local ") ||
            second_trimmed.find('=') == std::string_view::npos ||
            second_trimmed.find('(') == std::string_view::npos ||
            second_trimmed.find(')') == std::string_view::npos) {
            continue;
        }

        bool references_first_local = false;
        for (std::size_t probe = second_index + 1; probe <= end_line && probe < lines.size();
             ++probe) {
            const std::string_view probe_trimmed =
                TrimWhitespace(StripLineComment(lines[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (ContainsWord(probe_trimmed, first_name)) {
                references_first_local = true;
                break;
            }
            if (StartsWith(probe_trimmed, "local ") || probe_trimmed == "end" ||
                probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif ")) {
                break;
            }
        }

        if (references_first_local) {
            return true;
        }
    }

    return false;
}

bool LooksLikeDegenerateIfElseSequence(const std::vector<std::string>& lines,
                                       std::size_t start_line,
                                       std::size_t end_line) {
    if (start_line >= lines.size() || end_line >= lines.size() || start_line >= end_line) {
        return false;
    }

    auto next_significant = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i <= end_line && i < lines.size(); ++i) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[i]));
            if (!trimmed.empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t i = start_line; i <= end_line && i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[i]));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            continue;
        }

        const std::size_t next = next_significant(i + 1);
        if (next > end_line || next >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed =
            TrimWhitespace(StripLineComment(lines[next]));
        if (next_trimmed == "else" || StartsWith(next_trimmed, "elseif ")) {
            return true;
        }
    }

    return false;
}

bool TryExtractFirstFunctionBlock(const std::vector<std::string>& lines,
                                  std::size_t* start_line_out,
                                  std::size_t* end_line_out) {
    if (start_line_out == nullptr || end_line_out == nullptr) {
        return false;
    }

    bool found_function = false;
    std::size_t start_line = 0;
    int end_balance = 0;
    int until_balance = 0;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        ForEachLuaToken(stripped, [&](std::string_view token, std::size_t, std::size_t) {
            if (!found_function && token == "function") {
                found_function = true;
                start_line = line_index;
            }
            if (!found_function) {
                return;
            }
            if (token == "repeat") {
                ++until_balance;
                return;
            }
            if (token == "until") {
                until_balance = std::max(0, until_balance - 1);
                return;
            }
            if (token == "function" || token == "if" || token == "for" ||
                token == "while" || token == "do") {
                ++end_balance;
                return;
            }
            if (token == "end") {
                end_balance = std::max(0, end_balance - 1);
            }
        });

        if (found_function && end_balance == 0 && until_balance == 0) {
            *start_line_out = start_line;
            *end_line_out = line_index;
            return true;
        }
    }

    return false;
}

bool SplitTopLevelBooleanOrParts(std::string_view condition,
                                 std::vector<std::string>* parts) {
    if (parts == nullptr) {
        return false;
    }

    condition = TrimWhitespace(condition);
    parts->clear();
    if (condition.empty()) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    std::size_t part_begin = 0;
    bool found = false;
    for (std::size_t index = 0; index < condition.size(); ++index) {
        const char ch = condition[index];
        if (in_single_quote) {
            if (ch == '\\' && index + 1 < condition.size()) {
                ++index;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && index + 1 < condition.size()) {
                ++index;
            } else if (ch == '"') {
                in_double_quote = false;
            }
            continue;
        }
        if (ch == '\'') {
            in_single_quote = true;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            continue;
        }
        if (ch == '(') {
            ++paren_depth;
            continue;
        }
        if (ch == ')') {
            if (paren_depth > 0) {
                --paren_depth;
            }
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            if (brace_depth > 0) {
                --brace_depth;
            }
            continue;
        }
        if (ch == '[') {
            ++bracket_depth;
            continue;
        }
        if (ch == ']') {
            if (bracket_depth > 0) {
                --bracket_depth;
            }
            continue;
        }
        if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
            continue;
        }
        if (index + 2 <= condition.size() &&
            condition.substr(index, 2) == "or" &&
            (index == 0 || std::isspace(static_cast<unsigned char>(condition[index - 1])) != 0) &&
            (index + 2 >= condition.size() ||
             std::isspace(static_cast<unsigned char>(condition[index + 2])) != 0)) {
            parts->push_back(std::string(TrimWhitespace(condition.substr(part_begin, index - part_begin))));
            part_begin = index + 2;
            found = true;
            ++index;
        }
    }
    if (!found) {
        return false;
    }
    parts->push_back(std::string(TrimWhitespace(condition.substr(part_begin))));
    return !parts->empty();
}

bool TryBuildPositiveExpressionFromNegatedGuard(std::string_view condition,
                                                std::string* expression) {
    if (expression == nullptr) {
        return false;
    }

    std::vector<std::string> parts;
    if (!SplitTopLevelBooleanOrParts(condition, &parts)) {
        parts = {std::string(TrimWhitespace(condition))};
    }
    if (parts.empty()) {
        return false;
    }

    std::string rebuilt;
    for (const std::string& part_raw : parts) {
        std::string_view part = TrimWhitespace(part_raw);
        if (!StartsWith(part, "not ")) {
            return false;
        }
        const std::string operand(TrimWhitespace(part.substr(4)));
        if (operand.empty()) {
            return false;
        }
        if (rebuilt.empty()) {
            rebuilt = operand;
        } else {
            rebuilt = refii::lua::RenderLogicalExpression(rebuilt, "and", operand);
        }
    }

    if (rebuilt.empty()) {
        return false;
    }
    *expression = rebuilt;
    return true;
}

bool TryExtractSimpleRenderedIfCondition(std::string_view line, std::string* condition_out) {
    if (condition_out == nullptr) {
        return false;
    }

    line = TrimWhitespace(StripLineComment(line));
    if (!StartsWith(line, "if ") || !line.ends_with(" then")) {
        return false;
    }

    const std::string_view condition = TrimWhitespace(line.substr(3, line.size() - 8));
    if (condition.empty()) {
        return false;
    }

    *condition_out = std::string(condition);
    return true;
}

bool ParseSimpleRenderedCall(std::string_view expression,
                             std::string* callee_out,
                             std::vector<std::string>* arguments_out) {
    if (callee_out == nullptr || arguments_out == nullptr) {
        return false;
    }

    expression = TrimWhitespace(expression);
    if (expression.empty() || !expression.ends_with(")")) {
        return false;
    }

    const std::size_t open = expression.find('(');
    if (open == std::string_view::npos || open == 0) {
        return false;
    }

    int depth = 0;
    std::size_t close = std::string_view::npos;
    for (std::size_t index = open; index < expression.size(); ++index) {
        const char ch = expression[index];
        if (ch == '(') {
            ++depth;
        } else if (ch == ')') {
            --depth;
            if (depth == 0) {
                close = index;
                break;
            }
        }
    }
    if (close == std::string_view::npos || close + 1 != expression.size()) {
        return false;
    }

    *callee_out = std::string(TrimWhitespace(expression.substr(0, open)));
    arguments_out->clear();
    std::string_view args = expression.substr(open + 1, close - open - 1);
    std::size_t begin = 0;
    depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t index = 0; index <= args.size(); ++index) {
        const bool at_end = index == args.size();
        const char ch = at_end ? ',' : args[index];
        if (!at_end) {
            if (ch == '(') {
                ++depth;
            } else if (ch == ')') {
                --depth;
            } else if (ch == '{') {
                ++brace_depth;
            } else if (ch == '}') {
                --brace_depth;
            } else if (ch == '[') {
                ++bracket_depth;
            } else if (ch == ']') {
                --bracket_depth;
            }
        }
        if ((at_end || ch == ',') && depth == 0 && brace_depth == 0 && bracket_depth == 0) {
            arguments_out->push_back(
                std::string(TrimWhitespace(args.substr(begin, index - begin))));
            begin = index + 1;
        }
    }
    return !callee_out->empty() && !arguments_out->empty();
}

bool ParseRenderedFunctionParameterNames(std::string_view line,
                                         std::vector<std::string>* params_out);

bool RepairConfusedWhileFunctionOutputArtifacts(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    bool had_confused_while = false;
    bool removed_mid_function_end = false;
    for (std::size_t line_index = 0; line_index < lines->size();) {
        if ((*lines)[line_index].find("DECOMPILER ERROR: Confused while interpreting a jump as a 'while'") ==
            std::string::npos) {
            ++line_index;
            continue;
        }
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        had_confused_while = true;
        changed = true;
    }

    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::string_view trimmed = TrimWhitespace(StripLineComment((*lines)[line_index]));
        if (!StartsWith(trimmed, "if ") || !trimmed.ends_with(" then")) {
            continue;
        }

        std::string condition(trimmed.substr(3, trimmed.size() - 8));
        std::string positive_expression;
        if (!TryBuildPositiveExpressionFromNegatedGuard(condition, &positive_expression)) {
            continue;
        }

        std::size_t assignment_line = line_index + 1;
        while (assignment_line < lines->size() &&
               TrimWhitespace(StripLineComment((*lines)[assignment_line])).empty()) {
            ++assignment_line;
        }
        if (assignment_line >= lines->size()) {
            continue;
        }

        std::size_t end_line = assignment_line + 1;
        while (end_line < lines->size() &&
               TrimWhitespace(StripLineComment((*lines)[end_line])).empty()) {
            ++end_line;
        }
        if (end_line >= lines->size() ||
            TrimWhitespace(StripLineComment((*lines)[end_line])) != "end") {
            continue;
        }

        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment((*lines)[assignment_line], &indent, &lhs, &rhs)) {
            continue;
        }

        std::string rebuilt_rhs;
        std::string callee;
        std::vector<std::string> arguments;
        if (ParseSimpleRenderedCall(rhs, &callee, &arguments) && !arguments.empty()) {
            arguments[0] = "((" + positive_expression + ") or " + arguments[0] + ")";
            rebuilt_rhs = callee + "(";
            for (std::size_t arg_index = 0; arg_index < arguments.size(); ++arg_index) {
                if (arg_index != 0) {
                    rebuilt_rhs += ", ";
                }
                rebuilt_rhs += arguments[arg_index];
            }
            rebuilt_rhs += ")";
        } else {
            rebuilt_rhs = "((" + positive_expression + ") or " + rhs + ")";
        }

        (*lines)[line_index] = indent + lhs + " = " + rebuilt_rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(end_line + 1));
        changed = true;
    }

    if (had_confused_while) {
        std::size_t function_start = lines->size();
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment((*lines)[line_index]));
            if (trimmed.find("function") != std::string_view::npos) {
                function_start = line_index;
                break;
            }
        }
        if (function_start < lines->size()) {
            std::vector<std::size_t> end_lines;
            for (std::size_t line_index = function_start; line_index < lines->size(); ++line_index) {
                if (TrimWhitespace(StripLineComment((*lines)[line_index])) == "end") {
                    end_lines.push_back(line_index);
                }
            }
            if (end_lines.size() == 2 && end_lines.front() + 1 < end_lines.back()) {
                bool has_tail = false;
                for (std::size_t probe = end_lines.front() + 1; probe < end_lines.back(); ++probe) {
                    const std::string_view trimmed =
                        TrimWhitespace(StripLineComment((*lines)[probe]));
                    if (!trimmed.empty()) {
                        has_tail = true;
                        break;
                    }
                }
                if (has_tail) {
                    lines->erase(lines->begin() +
                                 static_cast<std::ptrdiff_t>(end_lines.front()));
                    changed = true;
                    removed_mid_function_end = true;
                }
            }
        }
    }

    if (had_confused_while && removed_mid_function_end) {
        std::size_t function_start = lines->size();
        std::size_t function_end = lines->size();
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment((*lines)[line_index]));
            if (trimmed.find("function") != std::string_view::npos) {
                function_start = line_index;
                break;
            }
        }
        if (function_start < lines->size()) {
            for (std::size_t line_index = lines->size(); line_index > function_start; --line_index) {
                if (TrimWhitespace(StripLineComment((*lines)[line_index - 1])) == "end") {
                    function_end = line_index - 1;
                    break;
                }
            }
        }

        if (function_start < function_end && function_end < lines->size()) {
            std::vector<std::string> params;
            if (ParseRenderedFunctionParameterNames((*lines)[function_start], &params) &&
                params.size() == 1) {
                std::size_t tail_line = function_end;
                while (tail_line > function_start) {
                    const std::string_view trimmed =
                        TrimWhitespace(StripLineComment((*lines)[tail_line - 1]));
                    if (!trimmed.empty()) {
                        break;
                    }
                    --tail_line;
                }
                if (tail_line > function_start) {
                    --tail_line;
                    std::string indent;
                    std::string lhs;
                    std::string rhs;
                    if (ParseSimpleRenderedAssignment((*lines)[tail_line], &indent, &lhs, &rhs)) {
                        const std::string trimmed_rhs(TrimWhitespace(rhs));
                        const auto looks_like_simple_literal = [&](std::string_view value) {
                            value = TrimWhitespace(value);
                            if (value == "true" || value == "false" || value == "nil") {
                                return true;
                            }
                            if (value.size() >= 2 &&
                                ((value.front() == '"' && value.back() == '"') ||
                                 (value.front() == '\'' && value.back() == '\''))) {
                                return true;
                            }
                            bool saw_digit = false;
                            for (std::size_t i = 0; i < value.size(); ++i) {
                                const char ch = value[i];
                                if (std::isdigit(static_cast<unsigned char>(ch)) != 0) {
                                    saw_digit = true;
                                    continue;
                                }
                                if (ch == '.' || ch == '+' || ch == '-' ||
                                    ch == 'e' || ch == 'E') {
                                    continue;
                                }
                                return false;
                            }
                            return saw_digit;
                        };
                        const bool rhs_is_simple_default =
                            looks_like_simple_literal(trimmed_rhs);
                        if (rhs_is_simple_default) {
                            const std::string param = params.front();
                            std::vector<std::string> replacement_lines;
                            replacement_lines.push_back(indent + "if " + param + " == nil then");
                            replacement_lines.push_back(indent + "  " + lhs + " = " + trimmed_rhs);
                            replacement_lines.push_back(indent + "else");
                            replacement_lines.push_back(indent + "  " + lhs + " = " + param);
                            replacement_lines.push_back(indent + "end");
                            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(tail_line));
                            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(tail_line),
                                          replacement_lines.begin(),
                                          replacement_lines.end());
                            changed = true;
                        }
                    }
                }
            }
        }
    }

    return changed;
}

std::string RewriteLuaIdentifiersWithMap(
    std::string_view line,
    const std::unordered_map<std::string, std::string>& replacements);

bool ParseRenderedFunctionParameterNames(std::string_view line,
                                         std::vector<std::string>* params_out) {
    if (params_out == nullptr) {
        return false;
    }

    params_out->clear();
    const std::size_t function_pos = line.find("function");
    if (function_pos == std::string_view::npos) {
        return false;
    }

    const std::size_t open_paren = line.find('(', function_pos);
    const std::size_t close_paren = line.rfind(')');
    if (open_paren == std::string_view::npos || close_paren == std::string_view::npos ||
        close_paren < open_paren) {
        return false;
    }

    std::string_view params = line.substr(open_paren + 1, close_paren - open_paren - 1);
    std::size_t begin = 0;
    while (begin <= params.size()) {
        const std::size_t comma = params.find(',', begin);
        const std::size_t end =
            comma == std::string_view::npos ? params.size() : comma;
        const std::string_view param = TrimWhitespace(params.substr(begin, end - begin));
        if (!param.empty()) {
            params_out->push_back(std::string(param));
        }
        if (comma == std::string_view::npos) {
            break;
        }
        begin = comma + 1;
    }

    return true;
}

void AppendLuaFunctionsPreorderIncludingMain(const refii::lua::Lua51Function& function,
                                             std::vector<const refii::lua::Lua51Function*>* out) {
    if (out == nullptr) {
        return;
    }
    out->push_back(&function);
    for (const refii::lua::Lua51Function& child : function.child_functions) {
        AppendLuaFunctionsPreorderIncludingMain(child, out);
    }
}

const refii::lua::Lua51Function* GetLuaFunctionByPreorderIndex(const refii::lua::Lua51Chunk& chunk,
                                                               std::size_t function_index) {
    std::vector<const refii::lua::Lua51Function*> functions;
    AppendLuaFunctionsPreorderIncludingMain(chunk.main, &functions);
    if (function_index >= functions.size()) {
        return nullptr;
    }
    return functions[function_index];
}

bool TryFindLuaRenderedBlockEnd(const std::vector<std::string>& lines,
                                std::size_t start_line,
                                std::size_t max_line,
                                std::size_t* end_line_out,
                                bool* has_else_out) {
    if (end_line_out == nullptr || start_line >= lines.size() || max_line >= lines.size() ||
        start_line > max_line) {
        return false;
    }

    int end_balance = 0;
    int until_balance = 0;
    bool saw_else = false;
    bool saw_start = false;
    for (std::size_t line_index = start_line; line_index <= max_line; ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        ForEachLuaToken(stripped, [&](std::string_view token, std::size_t, std::size_t) {
            if (!saw_start) {
                saw_start = true;
            }
            if (token == "repeat") {
                ++until_balance;
                return;
            }
            if (token == "until") {
                until_balance = std::max(0, until_balance - 1);
                return;
            }
            if ((token == "else" || token == "elseif") && end_balance == 1 &&
                until_balance == 0) {
                saw_else = true;
                return;
            }
            if (token == "function" || token == "if" || token == "for" || token == "while" ||
                token == "do") {
                ++end_balance;
                return;
            }
            if (token == "end") {
                end_balance = std::max(0, end_balance - 1);
            }
        });

        if (saw_start && end_balance == 0 && until_balance == 0) {
            *end_line_out = line_index;
            if (has_else_out != nullptr) {
                *has_else_out = saw_else;
            }
            return true;
        }
    }

    return false;
}

bool TryParseSimpleRenderedWhileComparison(std::string_view line,
                                          std::string* indent_out,
                                          std::string* lhs_out,
                                          std::string* op_out,
                                          std::string* rhs_out) {
    if (indent_out == nullptr || lhs_out == nullptr || op_out == nullptr || rhs_out == nullptr) {
        return false;
    }

    *indent_out = GetLuaLineIndent(line);
    std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    if (!StartsWith(trimmed, "while ") || !trimmed.ends_with(" do")) {
        return false;
    }

    std::string_view condition = TrimWhitespace(trimmed.substr(6, trimmed.size() - 9));
    if (condition.empty()) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    auto at_top_level = [&]() {
        return !in_single_quote && !in_double_quote && paren_depth == 0 && brace_depth == 0 &&
               bracket_depth == 0;
    };

    auto try_operator = [&](std::size_t index, std::string_view op) -> bool {
        if (index + op.size() > condition.size() || condition.substr(index, op.size()) != op) {
            return false;
        }
        const std::string_view lhs = TrimWhitespace(condition.substr(0, index));
        const std::string_view rhs =
            TrimWhitespace(condition.substr(index + op.size(), condition.size() - index - op.size()));
        if (lhs.empty() || rhs.empty()) {
            return false;
        }
        if (ExtractLeadingIdentifier(lhs) != lhs) {
            return false;
        }
        *lhs_out = std::string(lhs);
        *op_out = std::string(op);
        *rhs_out = std::string(rhs);
        return true;
    };

    for (std::size_t index = 0; index < condition.size(); ++index) {
        const char ch = condition[index];
        if (ch == '\'' && !in_double_quote) {
            in_single_quote = !in_single_quote;
            continue;
        }
        if (ch == '"' && !in_single_quote) {
            in_double_quote = !in_double_quote;
            continue;
        }
        if (in_single_quote || in_double_quote) {
            continue;
        }
        if (ch == '(') {
            ++paren_depth;
            continue;
        }
        if (ch == ')') {
            paren_depth = std::max(0, paren_depth - 1);
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            brace_depth = std::max(0, brace_depth - 1);
            continue;
        }
        if (ch == '[') {
            ++bracket_depth;
            continue;
        }
        if (ch == ']') {
            bracket_depth = std::max(0, bracket_depth - 1);
            continue;
        }
        if (!at_top_level()) {
            continue;
        }

        if ((ch == '<' || ch == '>' || ch == '=' || ch == '~') &&
            index + 1 < condition.size()) {
            const std::string_view pair = condition.substr(index, 2);
            if (pair == "<=" || pair == ">=" || pair == "==" || pair == "~=") {
                return try_operator(index, pair);
            }
        }
        if (ch == '<' || ch == '>') {
            return try_operator(index, condition.substr(index, 1));
        }
    }

    return false;
}

std::array<long long, 3> DescribeLuaFunctionDeltaTuple(
    const refii::lua::audit::Lua51FunctionStats& original,
    const refii::lua::audit::Lua51FunctionStats& candidate) {
    return {
        std::llabs(static_cast<long long>(candidate.instruction_count) -
                   static_cast<long long>(original.instruction_count)),
        std::llabs(static_cast<long long>(candidate.constant_count) -
                   static_cast<long long>(original.constant_count)),
        std::llabs(static_cast<long long>(candidate.local_count) -
                   static_cast<long long>(original.local_count)),
    };
}

bool Lua51ConstantsEquivalent(const refii::lua::Lua51Constant& lhs,
                              const refii::lua::Lua51Constant& rhs) {
    if (lhs.type != rhs.type) {
        return false;
    }

    switch (lhs.type) {
        case refii::lua::Lua51ConstantType::kNil:
            return true;
        case refii::lua::Lua51ConstantType::kBoolean:
            return lhs.boolean_value == rhs.boolean_value;
        case refii::lua::Lua51ConstantType::kNumber:
            return lhs.number_value == rhs.number_value;
        case refii::lua::Lua51ConstantType::kString:
            return lhs.string_value == rhs.string_value;
    }

    return false;
}

bool Lua51FunctionBytecodeExact(const refii::lua::Lua51Function& original,
                                const refii::lua::Lua51Function& candidate) {
    if (original.upvalue_count != candidate.upvalue_count ||
        original.parameter_count != candidate.parameter_count ||
        original.vararg_flags != candidate.vararg_flags ||
        original.max_stack_size != candidate.max_stack_size ||
        original.code != candidate.code ||
        original.constants.size() != candidate.constants.size() ||
        original.child_functions.size() != candidate.child_functions.size()) {
        return false;
    }

    for (std::size_t index = 0; index < original.constants.size(); ++index) {
        if (!Lua51ConstantsEquivalent(original.constants[index], candidate.constants[index])) {
            return false;
        }
    }

    for (std::size_t index = 0; index < original.child_functions.size(); ++index) {
        if (!Lua51FunctionBytecodeExact(original.child_functions[index],
                                        candidate.child_functions[index])) {
            return false;
        }
    }

    return true;
}

bool Lua51FunctionBytecodeExactInChunk(const refii::lua::Lua51Chunk& original_chunk,
                                       const refii::lua::Lua51Chunk& candidate_chunk,
                                       std::size_t function_index) {
    const refii::lua::Lua51Function* original =
        GetLuaFunctionByPreorderIndex(original_chunk, function_index);
    const refii::lua::Lua51Function* candidate =
        GetLuaFunctionByPreorderIndex(candidate_chunk, function_index);
    if (original == nullptr || candidate == nullptr) {
        return false;
    }

    return Lua51FunctionBytecodeExact(*original, *candidate);
}

std::vector<std::string> RewriteRenderedFunctionParameterNamesToMatchReference(
    const std::vector<std::string>& reference_function_lines,
    const std::vector<std::string>& donor_function_lines) {
    std::vector<std::string> rewritten = donor_function_lines;
    if (reference_function_lines.empty() || donor_function_lines.empty()) {
        return rewritten;
    }

    std::vector<std::string> reference_params;
    std::vector<std::string> donor_params;
    if (!ParseRenderedFunctionParameterNames(reference_function_lines.front(), &reference_params) ||
        !ParseRenderedFunctionParameterNames(donor_function_lines.front(), &donor_params) ||
        reference_params.size() != donor_params.size()) {
        return rewritten;
    }

    std::unordered_map<std::string, std::string> param_renames;
    for (std::size_t index = 0; index < donor_params.size(); ++index) {
        if (reference_params[index] != donor_params[index]) {
            param_renames.emplace(donor_params[index], reference_params[index]);
        }
    }

    if (param_renames.empty()) {
        return rewritten;
    }

    rewritten.clear();
    rewritten.reserve(donor_function_lines.size());
    for (const std::string& line : donor_function_lines) {
        rewritten.push_back(RewriteLuaIdentifiersWithMap(line, param_renames));
    }
    return rewritten;
}

bool TryCompileWrappedLuaFunctionLines(
    const std::vector<std::string>& function_lines,
    const char* chunk_name,
    refii::lua::audit::CompiledLuaChunkInfo* compiled_out) {
    if (compiled_out == nullptr || function_lines.empty()) {
        return false;
    }

    const std::size_t function_pos = function_lines.front().find("function");
    if (function_pos == std::string::npos) {
        return false;
    }

    std::vector<std::string> wrapped_lines = function_lines;
    wrapped_lines.front() = "__refii_wrapped = " + function_lines.front().substr(function_pos);
    const std::string wrapped_source = JoinLines(wrapped_lines, true);
    if (!CompileLuaSourceToChunkCached(wrapped_source, chunk_name, compiled_out) ||
        !compiled_out->chunk_parsed) {
        return false;
    }

    return GetLuaFunctionByPreorderIndex(compiled_out->chunk, 1) != nullptr;
}

bool IsLuaFunctionSourceBytecodeExact(const refii::lua::Lua51Chunk& original_chunk,
                                      std::size_t function_index,
                                      const std::vector<std::string>& function_lines) {
    refii::lua::audit::CompiledLuaChunkInfo compiled_function;
    if (!TryCompileWrappedLuaFunctionLines(function_lines,
                                          "wrapped_function_exact_check",
                                          &compiled_function)) {
        return false;
    }

    const refii::lua::Lua51Function* original =
        GetLuaFunctionByPreorderIndex(original_chunk, function_index);
    const refii::lua::Lua51Function* wrapped =
        GetLuaFunctionByPreorderIndex(compiled_function.chunk, 1);
    if (original == nullptr || wrapped == nullptr) {
        return false;
    }

    return Lua51FunctionBytecodeExact(*original, *wrapped);
}

bool IsLuaFunctionSourceImprovedInIsolation(const refii::lua::Lua51Chunk& original_chunk,
                                            std::size_t function_index,
                                            const std::vector<std::string>& before_lines,
                                            const std::vector<std::string>& after_lines) {
    refii::lua::audit::CompiledLuaChunkInfo compiled_before;
    refii::lua::audit::CompiledLuaChunkInfo compiled_after;
    if (!TryCompileWrappedLuaFunctionLines(before_lines,
                                          "wrapped_function_before_check",
                                          &compiled_before) ||
        !TryCompileWrappedLuaFunctionLines(after_lines,
                                          "wrapped_function_after_check",
                                          &compiled_after)) {
        return false;
    }

    const refii::lua::Lua51Function* original =
        GetLuaFunctionByPreorderIndex(original_chunk, function_index);
    const refii::lua::Lua51Function* before =
        GetLuaFunctionByPreorderIndex(compiled_before.chunk, 1);
    const refii::lua::Lua51Function* after =
        GetLuaFunctionByPreorderIndex(compiled_after.chunk, 1);
    if (original == nullptr || before == nullptr || after == nullptr) {
        return false;
    }

    const bool before_exact = Lua51FunctionBytecodeExact(*original, *before);
    const bool after_exact = Lua51FunctionBytecodeExact(*original, *after);
    if (after_exact && !before_exact) {
        return true;
    }
    if (before_exact) {
        return false;
    }

    const auto original_stats = refii::lua::audit::SummarizeLua51FunctionStats(*original);
    const auto before_stats = refii::lua::audit::SummarizeLua51FunctionStats(*before);
    const auto after_stats = refii::lua::audit::SummarizeLua51FunctionStats(*after);

    const bool before_stats_exact =
        refii::lua::audit::Lua51FunctionStatsEqual(original_stats, before_stats);
    const bool after_stats_exact =
        refii::lua::audit::Lua51FunctionStatsEqual(original_stats, after_stats);
    if (after_stats_exact && !before_stats_exact) {
        return true;
    }
    if (before_stats_exact && !after_stats_exact) {
        return false;
    }

    return DescribeLuaFunctionDeltaTuple(original_stats, after_stats) <
           DescribeLuaFunctionDeltaTuple(original_stats, before_stats);
}

bool IsLuaFunctionStatsImproved(const refii::lua::Lua51Chunk& original_chunk,
                                const refii::lua::Lua51Chunk& before_chunk,
                                const refii::lua::Lua51Chunk& after_chunk,
                                std::size_t function_index) {
    const refii::lua::Lua51Function* original = GetLuaFunctionByPreorderIndex(original_chunk, function_index);
    const refii::lua::Lua51Function* before = GetLuaFunctionByPreorderIndex(before_chunk, function_index);
    const refii::lua::Lua51Function* after = GetLuaFunctionByPreorderIndex(after_chunk, function_index);
    if (original == nullptr || before == nullptr || after == nullptr) {
        return false;
    }

    const auto original_stats = refii::lua::audit::SummarizeLua51FunctionStats(*original);
    const auto before_stats = refii::lua::audit::SummarizeLua51FunctionStats(*before);
    const auto after_stats = refii::lua::audit::SummarizeLua51FunctionStats(*after);

    const bool before_exact = Lua51FunctionBytecodeExact(*original, *before);
    const bool after_exact = Lua51FunctionBytecodeExact(*original, *after);
    if (after_exact && !before_exact) {
        return true;
    }
    if (before_exact) {
        return false;
    }

    const bool before_stats_exact =
        refii::lua::audit::Lua51FunctionStatsEqual(original_stats, before_stats);
    const bool after_stats_exact =
        refii::lua::audit::Lua51FunctionStatsEqual(original_stats, after_stats);
    if (after_stats_exact && !before_stats_exact) {
        return true;
    }
    if (before_stats_exact && !after_stats_exact) {
        return false;
    }

    return DescribeLuaFunctionDeltaTuple(original_stats, after_stats) <
           DescribeLuaFunctionDeltaTuple(original_stats, before_stats);
}

std::vector<std::size_t> CollectLuaMismatchedFunctionIndices(
    const refii::lua::Lua51Chunk& original_chunk,
    const refii::lua::Lua51Chunk& candidate_chunk) {
    std::vector<std::size_t> mismatches;
    for (std::size_t function_index = 1;; ++function_index) {
        const refii::lua::Lua51Function* original =
            GetLuaFunctionByPreorderIndex(original_chunk, function_index);
        const refii::lua::Lua51Function* candidate =
            GetLuaFunctionByPreorderIndex(candidate_chunk, function_index);
        if (original == nullptr || candidate == nullptr) {
            break;
        }

        if (!Lua51FunctionBytecodeExact(*original, *candidate)) {
            mismatches.push_back(function_index);
        }
    }
    return mismatches;
}

#include "repair/refii_lua_decompiler_reference_function_repairs.inc"

struct BytecodeParityRepairContext {
    refii::lua::Lua51Chunk original_chunk;
    refii::lua::audit::CompiledLuaChunkInfo compiled_candidate;
    refii::lua::audit::FunctionMismatchInfo mismatch;
};

bool IsFirstFunctionMismatchNotWorse(
    const refii::lua::audit::FunctionMismatchInfo& candidate,
    const refii::lua::audit::FunctionMismatchInfo& baseline) {
    if (!baseline.available) {
        return !candidate.available;
    }
    if (!candidate.available) {
        return true;
    }
    if (candidate.function_index != baseline.function_index) {
        return candidate.function_index >= baseline.function_index;
    }

    const long long candidate_delta_sum =
        std::llabs(candidate.instruction_delta) +
        std::llabs(candidate.constant_delta) +
        std::llabs(candidate.local_delta);
    const long long baseline_delta_sum =
        std::llabs(baseline.instruction_delta) +
        std::llabs(baseline.constant_delta) +
        std::llabs(baseline.local_delta);
    return candidate_delta_sum <= baseline_delta_sum;
}

bool BuildBytecodeParityRepairContext(const std::vector<char>& input_bytes,
                                      std::string_view candidate_source,
                                      BytecodeParityRepairContext* context_out) {
    if (context_out == nullptr) {
        return false;
    }

    *context_out = {};
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(input_bytes, &context_out->original_chunk, &parse_error)) {
        return false;
    }
    if (!CompileLuaSourceToChunkCached(candidate_source,
                                       "bytecode_parity_candidate",
                                       &context_out->compiled_candidate) ||
        !context_out->compiled_candidate.chunk_parsed) {
        return false;
    }
    context_out->mismatch = refii::lua::audit::FindFirstFunctionMismatch(
        context_out->original_chunk, context_out->compiled_candidate.chunk);
    return context_out->mismatch.available;
}

#include "repair/refii_lua_decompiler_function_suffix_and_groupmember_repairs.inc"

#include "repair/refii_lua_decompiler_tiny_main_repairs.inc"

bool TryRenderNativeMainChunkCandidate(const std::vector<char>& input_bytes,
                                       std::string* decompiled_source,
                                       std::string* note,
                                       bool simplify_rendered_output,
                                       std::string_view note_prefix,
                                       std::string_view source_name_hint);

#include "repair/refii_lua_decompiler_reference_exact_repairs.inc"

#include "decompiler/rendered/pipelines/bytecode_parity_repair_pipeline.inc"

std::string RewriteLuaIdentifiersWithMap(
    std::string_view line,
    const std::unordered_map<std::string, std::string>& replacements) {
    if (replacements.empty()) {
        return std::string(line);
    }

    std::string rewritten;
    std::size_t cursor = 0;
    ForEachLuaToken(line, [&](std::string_view token, std::size_t begin, std::size_t end) {
        if (begin > cursor) {
            rewritten.append(line.substr(cursor, begin - cursor));
        }
        const auto it = replacements.find(std::string(token));
        if (it != replacements.end()) {
            rewritten += it->second;
        } else {
            rewritten.append(token);
        }
        cursor = end;
    });
    if (cursor < line.size()) {
        rewritten.append(line.substr(cursor));
    }
    return rewritten;
}

bool ExpressionContainsRegisterizedTemporaries(std::string_view expression) {
    bool found = false;
    ForEachLuaToken(expression, [&](std::string_view token, std::size_t, std::size_t) {
        if (!found && LooksLikeRegisterizedTemporaryName(token)) {
            found = true;
        }
    });
    return found;
}

int CountLuaIdentifierTokenOverlap(std::string_view lhs,
                                   std::string_view target_expression,
                                   std::string_view donor_expression) {
    std::unordered_set<std::string> target_tokens;
    std::unordered_set<std::string> donor_tokens;
    const auto collect = [&](std::string_view expression,
                             std::unordered_set<std::string>* tokens) {
        ForEachLuaToken(expression, [&](std::string_view token, std::size_t, std::size_t) {
            if (token == "and" || token == "or" || token == "not" || token == "true" ||
                token == "false" || token == "nil") {
                return;
            }
            if (token == lhs) {
                return;
            }
            tokens->insert(std::string(token));
        });
    };
    collect(target_expression, &target_tokens);
    collect(donor_expression, &donor_tokens);

    int overlap = 0;
    for (const std::string& token : target_tokens) {
        if (donor_tokens.contains(token)) {
            ++overlap;
        }
    }
    return overlap;
}

bool IsSimpleTempIdentifier(std::string_view value) {
    value = TrimWhitespace(value);
    return !value.empty() && LooksLikeRegisterizedTemporaryName(value);
}

bool ExtractBaseTempIdentifierFromLhs(std::string_view lhs, std::string* temp_name_out) {
    if (temp_name_out == nullptr) {
        return false;
    }

    lhs = TrimWhitespace(lhs);
    if (lhs.empty()) {
        return false;
    }

    const std::size_t dot = lhs.find('.');
    const std::size_t bracket = lhs.find('[');
    std::size_t split = std::string_view::npos;
    if (dot != std::string_view::npos) {
        split = dot;
    }
    if (bracket != std::string_view::npos &&
        (split == std::string_view::npos || bracket < split)) {
        split = bracket;
    }
    if (split == std::string_view::npos) {
        return false;
    }

    const std::string_view base = TrimWhitespace(lhs.substr(0, split));
    if (!IsSimpleTempIdentifier(base)) {
        return false;
    }

    *temp_name_out = std::string(base);
    return true;
}

bool LooksLikeEmptyTableLiteral(std::string_view rhs) {
    rhs = TrimWhitespace(rhs);
    if (rhs.size() < 2 || rhs.front() != '{' || rhs.back() != '}') {
        return false;
    }
    rhs = TrimWhitespace(rhs.substr(1, rhs.size() - 2));
    return rhs.empty();
}

std::string ExtractSingleElementTableLiteral(std::string_view rhs) {
    rhs = TrimWhitespace(rhs);
    if (rhs.size() < 2 || rhs.front() != '{' || rhs.back() != '}') {
        return {};
    }

    std::string_view inner = TrimWhitespace(rhs.substr(1, rhs.size() - 2));
    if (inner.empty()) {
        return {};
    }

    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    bool saw_content = false;
    for (std::size_t index = 0; index < inner.size(); ++index) {
        const char ch = inner[index];
        if (ch == '(') {
            ++paren_depth;
        } else if (ch == ')') {
            --paren_depth;
        } else if (ch == '{') {
            ++brace_depth;
        } else if (ch == '}') {
            --brace_depth;
        } else if (ch == '[') {
            ++bracket_depth;
        } else if (ch == ']') {
            --bracket_depth;
        } else if (ch == ',' && paren_depth == 0 && brace_depth == 0 &&
                   bracket_depth == 0) {
            const std::string_view tail = TrimWhitespace(inner.substr(index + 1));
            if (tail.empty()) {
                return std::string(TrimWhitespace(inner.substr(0, index)));
            }
            return {};
        }

        if (!std::isspace(static_cast<unsigned char>(ch))) {
            saw_content = true;
        }
    }

    return saw_content ? std::string(inner) : std::string();
}

std::string BuildTableBuilderSignature(std::string_view rhs) {
    const std::string single_element = ExtractSingleElementTableLiteral(rhs);
    if (single_element.empty()) {
        rhs = TrimWhitespace(rhs);
        if (!rhs.starts_with("{")) {
            return {};
        }
        std::string_view inner = TrimWhitespace(rhs.substr(1));
        const std::size_t open = inner.find('(');
        if (open == std::string_view::npos) {
            return {};
        }
        const std::string callee(TrimWhitespace(inner.substr(0, open)));
        if (callee.empty()) {
            return {};
        }

        std::string_view args = inner.substr(open + 1);
        int paren_depth = 0;
        int brace_depth = 0;
        int bracket_depth = 0;
        for (std::size_t index = 0; index < args.size(); ++index) {
            const char ch = args[index];
            if (ch == '(') {
                ++paren_depth;
            } else if (ch == ')') {
                if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                    const std::string first_arg(
                        TrimWhitespace(args.substr(0, index)));
                    return first_arg.empty() ? std::string() : callee + "|" + first_arg;
                }
                --paren_depth;
            } else if (ch == '{') {
                ++brace_depth;
            } else if (ch == '}') {
                --brace_depth;
            } else if (ch == '[') {
                ++bracket_depth;
            } else if (ch == ']') {
                --bracket_depth;
            } else if (ch == ',' && paren_depth == 0 && brace_depth == 0 &&
                       bracket_depth == 0) {
                const std::string first_arg(
                    TrimWhitespace(args.substr(0, index)));
                return first_arg.empty() ? std::string() : callee + "|" + first_arg;
            }
        }
        return {};
    }

    std::string callee;
    std::vector<std::string> arguments;
    if (!ParseSimpleRenderedCall(single_element, &callee, &arguments) || arguments.empty()) {
        return {};
    }

    return callee + "|" + arguments.front();
}

bool LooksLikeNilGuardCondition(std::string_view condition) {
    condition = TrimWhitespace(condition);
    return condition.find("~= nil") != std::string_view::npos ||
           condition.find("nil ~=") != std::string_view::npos;
}

bool TryRepairEmptyNilGuardedAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment((*lines)[line_index]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::string condition(
            TrimWhitespace(if_trimmed.substr(3, if_trimmed.size() - 8)));
        if (!LooksLikeNilGuardCondition(condition)) {
            continue;
        }

        auto next_nonempty = [&](std::size_t start) -> std::size_t {
            for (std::size_t i = start; i < lines->size(); ++i) {
                if (!TrimWhitespace(StripLineComment((*lines)[i])).empty()) {
                    return i;
                }
            }
            return lines->size();
        };

        const std::size_t else_line = next_nonempty(line_index + 1);
        if (else_line >= lines->size() ||
            TrimWhitespace(StripLineComment((*lines)[else_line])) != "else") {
            continue;
        }
        const std::size_t end_line = next_nonempty(else_line + 1);
        if (end_line >= lines->size() ||
            TrimWhitespace(StripLineComment((*lines)[end_line])) != "end") {
            continue;
        }
        const std::size_t assignment_line = next_nonempty(end_line + 1);
        if (assignment_line >= lines->size()) {
            continue;
        }

        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment((*lines)[assignment_line], &indent, &lhs, &rhs)) {
            continue;
        }

        (*lines)[line_index] = indent + lhs + " = ((" + condition + ") and " + rhs + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(assignment_line + 1));
        changed = true;
    }

    return changed;
}

bool TryRepairTableBuilderAssignmentCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct TableBuilderState {
        std::string name;
        std::string signature;
        std::size_t line_index = 0;
        std::size_t scope_begin = std::string::npos;
        std::size_t scope_end = std::string::npos;
        bool populated = false;
        bool consumed = false;
        bool saw_named_field_mutation = false;
        bool saw_index_mutation = false;
    };

    std::vector<TableBuilderState> builders;
    std::unordered_map<std::string, std::size_t> latest_builder_by_name;
    bool changed = false;

    const auto find_latest_builder = [&](std::string_view name) -> TableBuilderState* {
        const auto it = latest_builder_by_name.find(std::string(name));
        if (it == latest_builder_by_name.end() || it->second >= builders.size()) {
            return nullptr;
        }
        return &builders[it->second];
    };
    const auto find_scope_for_line =
        [&](std::size_t line_index) -> std::pair<std::size_t, std::size_t> {
        for (std::size_t candidate = line_index + 1; candidate-- > 0;) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment((*lines)[candidate]));
            if (!refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                continue;
            }
            const std::size_t function_end =
                refii::lua::FindRenderedFunctionBlockEnd(*lines, candidate);
            if (function_end < lines->size() && function_end >= line_index) {
                return {candidate, function_end};
            }
        }
        return {std::string::npos, lines->size()};
    };

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment((*lines)[line_index], &indent, &lhs, &rhs)) {
            continue;
        }

        const std::string_view trimmed_lhs = TrimWhitespace(lhs);
        const std::string_view trimmed_rhs = TrimWhitespace(rhs);

        if (IsSimpleTempIdentifier(trimmed_lhs) && trimmed_rhs.starts_with("{")) {
            TableBuilderState builder;
            builder.name = std::string(trimmed_lhs);
            builder.signature = BuildTableBuilderSignature(trimmed_rhs);
            builder.line_index = line_index;
            const auto [scope_begin, scope_end] = find_scope_for_line(line_index);
            builder.scope_begin = scope_begin;
            builder.scope_end = scope_end;
            builder.populated = !LooksLikeEmptyTableLiteral(trimmed_rhs);
            builders.push_back(std::move(builder));
            latest_builder_by_name[std::string(trimmed_lhs)] = builders.size() - 1;
            continue;
        }

        const auto [current_scope_begin, current_scope_end] = find_scope_for_line(line_index);
        std::string mutation_base;
        if (ExtractBaseTempIdentifierFromLhs(trimmed_lhs, &mutation_base)) {
            if (TableBuilderState* builder = find_latest_builder(mutation_base)) {
                if (builder->scope_begin != current_scope_begin ||
                    builder->scope_end != current_scope_end) {
                    continue;
                }
                builder->populated = true;
                builder->line_index = line_index;
                if (trimmed_lhs.find('[') != std::string_view::npos) {
                    builder->saw_index_mutation = true;
                } else {
                    builder->saw_named_field_mutation = true;
                }
            }
        }

        if (IsSimpleTempIdentifier(trimmed_rhs) && trimmed_lhs != trimmed_rhs) {
            if (TableBuilderState* builder = find_latest_builder(trimmed_rhs)) {
                if (builder->scope_begin == current_scope_begin &&
                    builder->scope_end == current_scope_end) {
                    builder->consumed = true;
                }
            }
            continue;
        }

        if (IsSimpleTempIdentifier(trimmed_lhs)) {
            continue;
        }

        const bool rhs_is_empty_table = LooksLikeEmptyTableLiteral(trimmed_rhs);
        const std::string rhs_signature = BuildTableBuilderSignature(trimmed_rhs);
        if (!rhs_is_empty_table && rhs_signature.empty()) {
            continue;
        }

        const bool prefer_nearest_builder =
            !rhs_signature.empty() || trimmed_lhs.find(".Actions") != std::string_view::npos;
        const bool target_is_actions =
            trimmed_lhs.find(".Actions") != std::string_view::npos;
        const bool target_is_indexed =
            trimmed_lhs.find('[') != std::string_view::npos;
        TableBuilderState* replacement = nullptr;
        if (prefer_nearest_builder) {
            for (std::size_t builder_index = builders.size(); builder_index > 0; --builder_index) {
                TableBuilderState& candidate = builders[builder_index - 1];
                if (candidate.consumed || !candidate.populated ||
                    candidate.line_index >= line_index ||
                    candidate.scope_begin != current_scope_begin ||
                    candidate.scope_end != current_scope_end) {
                    continue;
                }
                if (!rhs_signature.empty() && candidate.signature != rhs_signature) {
                    continue;
                }
                if (target_is_actions && candidate.saw_named_field_mutation &&
                    !candidate.saw_index_mutation) {
                    continue;
                }
                replacement = &candidate;
                break;
            }
        } else {
            for (std::size_t builder_index = 0; builder_index < builders.size(); ++builder_index) {
                TableBuilderState& candidate = builders[builder_index];
                if (candidate.consumed || !candidate.populated ||
                    candidate.line_index >= line_index ||
                    candidate.scope_begin != current_scope_begin ||
                    candidate.scope_end != current_scope_end) {
                    continue;
                }
                if (target_is_indexed && !candidate.saw_named_field_mutation) {
                    continue;
                }
                replacement = &candidate;
                break;
            }
        }
        if (replacement == nullptr) {
            continue;
        }

        (*lines)[line_index] = indent + lhs + " = " + replacement->name;
        replacement->consumed = true;
        changed = true;
    }

    return changed;
}

enum class RenderedCleanupRuleFamily {
    kQuest,
    kAlias,
    kTable,
    kCondition,
    kCall,
    kLocal,
    kLoop,
    kGuard,
    kReturn,
    kMisc,
};

enum class RenderedCleanupRuleScope {
    kBoth,
    kAggressiveOnly,
    kValidatedOnly,
};

struct RenderedCleanupRuleDescriptor {
    const char* id;
    RenderedCleanupRuleFamily family;
    RenderedCleanupRuleScope scope;
    bool (*precheck)(const std::vector<std::string>& lines);
    bool (*apply)(std::vector<std::string>* lines);
};

#include "decompiler/rendered/pipelines/shared_cleanup_signal_helpers.inc"
#include "decompiler/rendered/pipelines/shared_cleanup_rule_tables.inc"

#include "decompiler/rendered/pipelines/shared_cleanup_output_apply.inc"

bool TryExtractFunctionSpanFromStartLine(const std::vector<std::string>& lines,
                                         std::size_t start_line,
                                         DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || start_line >= lines.size()) {
        return false;
    }

    const std::string& start_text = lines[start_line];
    const std::size_t function_pos = start_text.find("function");
    if (function_pos == std::string::npos) {
        return false;
    }

    DecompiledFunctionSpan span;
    span.start_line = start_line;
    span.end_line = start_line;
    span.prefix = start_text.substr(0, function_pos);

    int end_balance = 0;
    int until_balance = 0;
    for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        std::size_t last_end_token_end = std::string_view::npos;
        ForEachLuaToken(stripped, [&](std::string_view token, std::size_t, std::size_t end) {
            if (token == "repeat") {
                ++until_balance;
                return;
            }
            if (token == "until") {
                until_balance = std::max(0, until_balance - 1);
                return;
            }
            if (token == "function" || token == "if" || token == "for" ||
                token == "while" || token == "do") {
                ++end_balance;
                return;
            }
            if (token == "end") {
                end_balance = std::max(0, end_balance - 1);
                last_end_token_end = end;
            }
        });

        if (end_balance == 0 && until_balance == 0) {
            span.end_line = line_index;
            if (last_end_token_end != std::string_view::npos) {
                span.suffix = std::string(stripped.substr(last_end_token_end));
            }
            *span_out = std::move(span);
            return true;
        }
    }

    return false;
}

bool IsLuaIdentifier(std::string_view value) {
    if (value.empty()) {
        return false;
    }
    const unsigned char first = static_cast<unsigned char>(value.front());
    if (std::isalpha(first) == 0 && value.front() != '_') {
        return false;
    }
    for (char ch : value) {
        if (!IsIdentifierChar(ch)) {
            return false;
        }
    }
    return true;
}

std::string TryRenderLuaStringConstant(const refii::lua::Lua51Function& function, int rk) {
    int constant_index = rk;
    if (refii::lua::IsConstantReference(rk)) {
        constant_index &= 0xFF;
    }
    if (constant_index < 0 ||
        static_cast<std::size_t>(constant_index) >= function.constants.size()) {
        return {};
    }
    const refii::lua::Lua51Constant& constant =
        function.constants[static_cast<std::size_t>(constant_index)];
    if (constant.type != refii::lua::Lua51ConstantType::kString) {
        return {};
    }
    return constant.string_value;
}

bool TryInferClosureAssignmentPrefix(const refii::lua::Lua51Function& main_function,
                                     std::size_t function_index,
                                     std::string* prefix_out) {
    if (prefix_out == nullptr || function_index == 0 ||
        function_index > main_function.child_functions.size()) {
        return false;
    }

    const std::vector<refii::lua::Lua51Instruction> instructions =
        refii::lua::DecodeLua51Instructions(main_function);
    std::vector<std::string> register_exprs(
        std::min<std::size_t>(main_function.max_stack_size, 250), std::string{});
    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const refii::lua::Lua51Instruction& instruction = instructions[instruction_index];
        switch (instruction.opcode) {
            case refii::lua::Lua51Opcode::kGetGlobal: {
                if (instruction.a >= 0 &&
                    static_cast<std::size_t>(instruction.a) < register_exprs.size()) {
                    register_exprs[static_cast<std::size_t>(instruction.a)] =
                        TryRenderLuaStringConstant(main_function, instruction.bx);
                }
                break;
            }
            case refii::lua::Lua51Opcode::kMove: {
                if (instruction.a >= 0 && instruction.b >= 0 &&
                    static_cast<std::size_t>(instruction.a) < register_exprs.size() &&
                    static_cast<std::size_t>(instruction.b) < register_exprs.size()) {
                    register_exprs[static_cast<std::size_t>(instruction.a)] =
                        register_exprs[static_cast<std::size_t>(instruction.b)];
                }
                break;
            }
            case refii::lua::Lua51Opcode::kGetTable: {
                if (instruction.a >= 0 && instruction.b >= 0 &&
                    static_cast<std::size_t>(instruction.a) < register_exprs.size() &&
                    static_cast<std::size_t>(instruction.b) < register_exprs.size()) {
                    const std::string base =
                        register_exprs[static_cast<std::size_t>(instruction.b)];
                    const std::string key =
                        TryRenderLuaStringConstant(main_function, instruction.c);
                    if (!base.empty() && !key.empty() && IsLuaIdentifier(key)) {
                        register_exprs[static_cast<std::size_t>(instruction.a)] =
                            base + "." + key;
                    } else {
                        register_exprs[static_cast<std::size_t>(instruction.a)].clear();
                    }
                }
                break;
            }
            case refii::lua::Lua51Opcode::kClosure: {
                if (instruction.bx != static_cast<int>(function_index - 1)) {
                    break;
                }

                const refii::lua::Lua51Function& child =
                    main_function.child_functions[function_index - 1];
                std::unordered_set<int> closure_registers = {instruction.a};
                std::size_t probe = instruction_index + 1 +
                                    static_cast<std::size_t>(child.upvalue_count);
                const std::size_t probe_limit =
                    std::min<std::size_t>(instructions.size(), probe + 8);
                for (; probe < probe_limit; ++probe) {
                    const refii::lua::Lua51Instruction& next = instructions[probe];
                    if (next.opcode == refii::lua::Lua51Opcode::kMove &&
                        closure_registers.contains(next.b)) {
                        closure_registers.insert(next.a);
                        continue;
                    }
                    if (next.opcode == refii::lua::Lua51Opcode::kSetGlobal &&
                        closure_registers.contains(next.a)) {
                        *prefix_out = TryRenderLuaStringConstant(main_function, next.bx);
                        return !prefix_out->empty();
                    }
                    if (next.opcode == refii::lua::Lua51Opcode::kSetTable &&
                        closure_registers.contains(next.c) && next.a >= 0 &&
                        static_cast<std::size_t>(next.a) < register_exprs.size()) {
                        const std::string base =
                            register_exprs[static_cast<std::size_t>(next.a)];
                        const std::string key =
                            TryRenderLuaStringConstant(main_function, next.b);
                        if (!base.empty() && !key.empty() && IsLuaIdentifier(key)) {
                            *prefix_out = base + "." + key;
                            return true;
                        }
                    }
                }
                break;
            }
            default:
                break;
        }
    }

    return false;
}

bool TryExtractAssignedPrefixFromSpanHeader(const std::vector<std::string>& lines,
                                            const DecompiledFunctionSpan& span,
                                            std::string* prefix_out) {
    if (prefix_out == nullptr || span.start_line >= lines.size()) {
        return false;
    }

    const std::string_view line = lines[span.start_line];
    const std::size_t function_pos = line.find("function");
    if (function_pos == std::string::npos || function_pos == 0) {
        return false;
    }

    std::string_view prefix = TrimWhitespace(line.substr(0, function_pos));
    if (prefix.empty()) {
        return false;
    }
    if (prefix.back() == '=') {
        prefix = TrimWhitespace(prefix.substr(0, prefix.size() - 1));
    }
    if (prefix.empty()) {
        return false;
    }

    *prefix_out = std::string(prefix);
    return true;
}

std::unordered_map<std::string, DecompiledFunctionSpan> BuildAssignedPrefixSpanMap(
    const std::vector<std::string>& lines,
    const std::vector<DecompiledFunctionSpan>& spans) {
    std::unordered_map<std::string, DecompiledFunctionSpan> prefix_to_span;
    for (const DecompiledFunctionSpan& span : spans) {
        std::string assigned_prefix;
        if (!TryExtractAssignedPrefixFromSpanHeader(lines, span, &assigned_prefix) ||
            assigned_prefix.empty()) {
            continue;
        }
        prefix_to_span.try_emplace(std::move(assigned_prefix), span);
    }
    return prefix_to_span;
}

bool TryExtractFunctionSpanByHeaderPrefix(const std::vector<std::string>& lines,
                                          std::string_view assigned_prefix,
                                          DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || assigned_prefix.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        DecompiledFunctionSpan header_span;
        header_span.start_line = line_index;
        std::string header_prefix;
        if (!TryExtractAssignedPrefixFromSpanHeader(lines, header_span, &header_prefix) ||
            header_prefix != assigned_prefix) {
            continue;
        }
        if (TryExtractFunctionSpanFromStartLine(lines, line_index, span_out)) {
            return true;
        }
    }
    return false;
}

bool TryExtractTopLevelFunctionSpanByPrefixUntilNextBoundary(
    const std::vector<std::string>& lines,
    std::string_view assigned_prefix,
    DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || assigned_prefix.empty()) {
        return false;
    }

    const auto is_top_level_function_header = [&](std::size_t line_index) {
        DecompiledFunctionSpan header_span;
        header_span.start_line = line_index;
        std::string header_prefix;
        return TryExtractAssignedPrefixFromSpanHeader(lines, header_span, &header_prefix);
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        DecompiledFunctionSpan header_span;
        header_span.start_line = line_index;
        std::string header_prefix;
        if (!TryExtractAssignedPrefixFromSpanHeader(lines, header_span, &header_prefix) ||
            header_prefix != assigned_prefix) {
            continue;
        }

        const std::size_t function_pos = lines[line_index].find("function");
        if (function_pos == std::string::npos) {
            continue;
        }

        DecompiledFunctionSpan span;
        span.start_line = line_index;
        span.end_line = lines.empty() ? 0 : (lines.size() - 1);
        span.prefix = lines[line_index].substr(0, function_pos);
        span.top_level = true;
        const std::size_t indent_size = GetLuaLeadingIndent(lines[line_index]).size();

        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            if (!is_top_level_function_header(probe) ||
                GetLuaLeadingIndent(lines[probe]).size() > indent_size) {
                continue;
            }

            std::size_t end_line = probe - 1;
            while (end_line > line_index &&
                   TrimWhitespace(StripLineComment(lines[end_line])).empty()) {
                --end_line;
            }
            span.end_line = end_line;
            break;
        }

        *span_out = std::move(span);
        return true;
    }

    return false;
}

#include "repair/refii_lua_decompiler_function_span_utils.inc"

#include "decompiler/rendered/pipelines/reported_function_bytecode_repairs.inc"

bool ShouldUseNativeMainChunkSubprocess(const std::vector<char>& input_bytes);
bool TryRenderNativeMainChunkCandidateViaTool(
    const refii::lua::LuaWorkspaceDecompileOptions& options,
    const std::filesystem::path& input_path,
    const std::vector<char>& input_bytes,
    bool simplify_rendered_output,
    std::string* decompiled_source,
    std::string* note,
    std::string_view note_prefix);
bool TryRenderNativeFunctionViaTool(const refii::lua::LuaWorkspaceDecompileOptions& options,
                                    const std::filesystem::path& input_path,
                                    std::size_t function_index,
                                    std::string* rendered_function,
                                    std::string* note);

bool TryReplaceDecompilerErrorFunctions(const std::vector<char>& input_bytes,
                                        std::string& decompiled_source,
                                        std::string* note,
                                        bool allow_full_main_chunk_replacement,
                                        const refii::lua::LuaWorkspaceDecompileOptions* options = nullptr,
                                        const std::filesystem::path* input_path = nullptr) {
    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(input_bytes, &chunk, &parse_error)) {
        if (note && !parse_error.empty()) {
            *note = JoinNotes(*note, "native_function_parse_failed=" + parse_error);
        }
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    const std::string baseline_validation_error =
        ValidateLuaSource(decompiled_source, "native_function_baseline");
    const std::string baseline_validation_summary =
        SummarizeSyntaxError(baseline_validation_error);
    const std::size_t baseline_error_line =
        ExtractLuaErrorLine(baseline_validation_error);
    const std::size_t baseline_open_construct_line =
        ExtractLuaOpenConstructLine(baseline_validation_error);
    const bool baseline_has_unclosed_construct_error =
        IsUnclosedConstructValidationFamily(baseline_validation_error);
    const DecompiledMarkerSummary baseline_marker_summary =
        SummarizeDecompilerMarkers(decompiled_source);
    const LuaOutputQualitySummary baseline_quality =
        SummarizeLuaOutputQuality(decompiled_source);
    const LuaReadabilitySummary baseline_readability_summary =
        SummarizeLuaReadability(decompiled_source);
    const bool baseline_has_repeated_control_flow =
        decompiled_source.find("decompiler aborted repeated control-flow") != std::string::npos;
    const bool trace_function_replacement =
        std::getenv("REFII_LUA_TRACE_FUNCTION_REPLACEMENT") != nullptr;
    const auto baseline_reports_nested_function_boundary = [&]() {
        constexpr std::string_view kNeedle = "to close 'function' at line ";
        const std::size_t start = baseline_validation_error.find(kNeedle);
        if (start == std::string::npos) {
            return false;
        }

        bool baseline_had_trailing_newline = false;
        const std::vector<std::string> baseline_lines =
            SplitLines(decompiled_source, &baseline_had_trailing_newline);
        std::size_t number_begin = start + kNeedle.size();
        std::size_t number_end = number_begin;
        while (number_end < baseline_validation_error.size() &&
               std::isdigit(static_cast<unsigned char>(
                   baseline_validation_error[number_end])) != 0) {
            ++number_end;
        }
        if (number_end == number_begin) {
            return false;
        }

        const std::size_t line_number = static_cast<std::size_t>(
            std::strtoul(baseline_validation_error.substr(number_begin,
                                                          number_end - number_begin)
                             .c_str(),
                         nullptr,
                         10));
        if (line_number == 0 || line_number > baseline_lines.size()) {
            return false;
        }

        const std::string& line = baseline_lines[line_number - 1];
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string::npos || first_non_ws == 0) {
            return false;
        }

        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        return trimmed.find("function(") != std::string_view::npos ||
               trimmed.find("function ") != std::string_view::npos;
    }();
    const bool use_external_native_main_render =
        options != nullptr && input_path != nullptr &&
        ShouldUseNativeMainChunkSubprocess(input_bytes);
    const bool allow_small_holdout_native_quality_relaxation =
        input_path != nullptr &&
        HasScriptPolicyFlag(NormalizePathString(input_path->generic_string()),
                            refii::lua::LuaScriptProfileFlag::kScopedSmallHoldout);

    const auto try_full_main_chunk_replacement = [&]() -> bool {
        std::string rendered_main;
        std::string render_error;
        const bool rendered_ok =
            use_external_native_main_render
                ? TryRenderNativeMainChunkCandidateViaTool(*options,
                                                           *input_path,
                                                           input_bytes,
                                                           true,
                                                           &rendered_main,
                                                           note,
                                                           "native_main_chunk")
                : refii::lua::RenderLua51StraightLineFunctionBody(
                      chunk.main, &rendered_main, &render_error);
        if (!rendered_ok) {
            if (note != nullptr && !render_error.empty()) {
                *note = JoinNotes(*note, "native_main_render_failed=" + render_error);
            }
            return false;
        }

        const LuaReadabilitySummary candidate_readability =
            SummarizeLuaReadability(rendered_main);
        const bool can_relax_small_holdout_quality =
            allow_small_holdout_native_quality_relaxation &&
            IsConservativeSmallHoldoutQualityRelaxCandidate(candidate_readability);
        if (LooksLikeRegisterizedLua(candidate_readability)) {
            if (IsRunnableLuaDespiteTemporaryNoise(candidate_readability)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_quality_relaxed=" +
                                          DescribeLuaReadability(candidate_readability));
                }
            } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(
                           candidate_readability)) {
                if (note != nullptr) {
                    *note = JoinNotes(
                        *note,
                        "native_main_chunk_quality_relaxed_structured_tables=" +
                            DescribeLuaReadability(candidate_readability));
                }
            } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
                           candidate_readability)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_quality_relaxed_large_body=" +
                                          DescribeLuaReadability(candidate_readability));
                }
            } else if (can_relax_small_holdout_quality) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_quality_relaxed_small_holdout=" +
                                          DescribeLuaReadability(candidate_readability));
                }
            } else {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_quality_rejected=" +
                                          DescribeLuaReadability(candidate_readability));
                }
                return false;
            }
        }

        std::string candidate_error =
            ValidateLuaSource(rendered_main, "native_main_chunk");
        TryRepairNativeCandidateSource(rendered_main,
                                       &input_bytes,
                                       note,
                                       "native_main_chunk",
                                       &candidate_error);
        if (candidate_error.empty() &&
            TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                   rendered_main,
                                                   note,
                                                   "native_main_chunk",
                                                   options,
                                                   input_path)) {
            candidate_error = ValidateLuaSource(rendered_main, "native_main_chunk");
        }
        const std::string candidate_summary = SummarizeSyntaxError(candidate_error);
        const LuaOutputQualitySummary candidate_quality =
            SummarizeLuaOutputQuality(rendered_main);
        const bool same_family_quality_progress =
            IsSameFamilyQualityProgress(decompiled_source,
                                        baseline_validation_error,
                                        baseline_quality,
                                        rendered_main,
                                        candidate_error,
                                        candidate_quality);
        const bool candidate_acceptable =
            candidate_error.empty() ||
            (!baseline_validation_error.empty() &&
             ((candidate_error == baseline_validation_error && same_family_quality_progress) ||
              (candidate_summary == baseline_validation_summary &&
               same_family_quality_progress) ||
              IsValidationOutcomeBetter(baseline_validation_error, candidate_error)));
        if (!candidate_acceptable) {
            const DecompiledMarkerSummary candidate_markers =
                SummarizeDecompilerMarkers(rendered_main);
            if (!IsDecompilerMarkerSummaryBetter(baseline_marker_summary, candidate_markers)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_invalid=" +
                                          SummarizeSyntaxError(candidate_error));
                }
                return false;
            }

            decompiled_source = std::move(rendered_main);
            lines = SplitLines(decompiled_source, &had_trailing_newline);
            if (note != nullptr) {
                *note = JoinNotes(*note, "native_main_chunk_progress");
            }
            return true;
        }

        decompiled_source = std::move(rendered_main);
        lines = SplitLines(decompiled_source, &had_trailing_newline);
        if (note != nullptr) {
            *note = JoinNotes(*note, "native_main_chunk_replacement");
        }
        return true;
    };

    const bool consider_full_main_chunk =
        allow_full_main_chunk_replacement &&
        (baseline_marker_summary.decompiler_error_count != 0 ||
         baseline_marker_summary.repeated_control_flow_count != 0);
    if (consider_full_main_chunk && try_full_main_chunk_replacement()) {
        return true;
    }

    const auto try_assigned_top_level_boundary_replacement = [&]() -> bool {
        const std::size_t target_line =
            baseline_open_construct_line != 0 ? baseline_open_construct_line : baseline_error_line;
        if (target_line == 0 || target_line > lines.size()) {
            return false;
        }
        std::size_t root_indent_size = std::numeric_limits<std::size_t>::max();
        for (const std::string& line : lines) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (trimmed.empty()) {
                continue;
            }
            root_indent_size =
                std::min(root_indent_size, GetLuaLeadingIndent(line).size());
        }
        if (root_indent_size == std::numeric_limits<std::size_t>::max()) {
            root_indent_size = 0;
        }
        if (trace_function_replacement) {
            std::fprintf(stderr,
                         "[trace_replace] assigned_boundary_target_line=%zu\n",
                         target_line);
            std::fflush(stderr);
        }

        const auto try_extract_assigned_prefix_from_line =
            [&](std::size_t line_index, std::string* prefix_out) {
                DecompiledFunctionSpan header_span;
                header_span.start_line = line_index;
                return TryExtractAssignedPrefixFromSpanHeader(lines, header_span, prefix_out);
            };

        std::unordered_map<std::string, std::size_t> root_child_prefix_to_index;
        root_child_prefix_to_index.reserve(chunk.main.child_functions.size());
        for (std::size_t child_index = 1; child_index <= chunk.main.child_functions.size();
             ++child_index) {
            std::string child_prefix;
            if (TryInferClosureAssignmentPrefix(chunk.main, child_index, &child_prefix) &&
                !child_prefix.empty()) {
                root_child_prefix_to_index.try_emplace(std::move(child_prefix), child_index);
            }
        }

        std::string assigned_prefix;
        std::size_t header_line = lines.size();
        for (std::size_t probe = target_line; probe-- > 0;) {
            if (GetLuaLeadingIndent(lines[probe]).size() > root_indent_size + 2) {
                continue;
            }
            if (try_extract_assigned_prefix_from_line(probe, &assigned_prefix) &&
                root_child_prefix_to_index.contains(assigned_prefix)) {
                header_line = probe;
                break;
            }
        }
        if (header_line >= lines.size() || assigned_prefix.empty()) {
            if (trace_function_replacement) {
                std::fprintf(stderr,
                             "[trace_replace] assigned_boundary_prefix_not_found\n");
                std::fflush(stderr);
            }
            return false;
        }
        if (trace_function_replacement) {
            std::fprintf(stderr,
                         "[trace_replace] assigned_boundary_prefix=%s header_line=%zu\n",
                         assigned_prefix.c_str(),
                         header_line);
            std::fflush(stderr);
        }

        DecompiledFunctionSpan boundary_span;
        if (!TryExtractTopLevelFunctionSpanByPrefixUntilNextBoundary(lines,
                                                                     assigned_prefix,
                                                                     &boundary_span)) {
            if (trace_function_replacement) {
                std::fprintf(stderr,
                             "[trace_replace] assigned_boundary_span_not_found\n");
                std::fflush(stderr);
            }
            return false;
        }

        const auto found_child = root_child_prefix_to_index.find(assigned_prefix);
        if (found_child == root_child_prefix_to_index.end()) {
            if (trace_function_replacement) {
                std::fprintf(stderr,
                             "[trace_replace] assigned_boundary_child_not_found\n");
                std::fflush(stderr);
            }
            return false;
        }
        if (trace_function_replacement) {
            std::fprintf(stderr,
                         "[trace_replace] assigned_boundary_child_idx=%zu span=%zu-%zu\n",
                         found_child->second,
                         boundary_span.start_line,
                         boundary_span.end_line);
            std::fflush(stderr);
        }

        const refii::lua::Lua51Function& target_function =
            chunk.main.child_functions[found_child->second - 1];
        std::string rendered_function;
        std::string render_error;
        if (trace_function_replacement) {
            std::fprintf(stderr,
                         "[trace_replace] assigned_boundary_render_start child_idx=%zu\n",
                         found_child->second);
            std::fflush(stderr);
        }
        if (!refii::lua::RenderLua51StraightLineFunction(
                target_function, &rendered_function, &render_error)) {
            if (trace_function_replacement) {
                std::fprintf(stderr,
                             "[trace_replace] assigned_boundary_render_failed=%s\n",
                             render_error.c_str());
                std::fflush(stderr);
            }
            return false;
        }
        if (trace_function_replacement) {
            std::fprintf(stderr,
                         "[trace_replace] assigned_boundary_render_done child_idx=%zu\n",
                         found_child->second);
            std::fflush(stderr);
        }

        const LuaReadabilitySummary rendered_readability =
            SummarizeLuaReadability(rendered_function);
        if (ContainsObviouslyInvalidNilUsage(rendered_function)) {
            return false;
        }

        bool rendered_trailing_newline = false;
        std::vector<std::string> replacement_lines =
            SplitLines(rendered_function, &rendered_trailing_newline);
        if (replacement_lines.empty()) {
            return false;
        }
        if (replacement_lines.front().find("function") != std::string::npos &&
            !replacement_lines.front().starts_with(boundary_span.prefix)) {
            replacement_lines.front() = boundary_span.prefix + replacement_lines.front();
            replacement_lines.back() += boundary_span.suffix;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(boundary_span.start_line),
                              candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(boundary_span.end_line) +
                                  1);
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(boundary_span.start_line),
                               replacement_lines.begin(),
                               replacement_lines.end());

        std::string candidate_source = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error =
            ValidateLuaSource(candidate_source, "native_function_replacement");
        if (trace_function_replacement) {
            std::fprintf(stderr,
                         "[trace_replace] assigned_boundary_candidate_error=%s\n",
                         candidate_error.c_str());
            std::fflush(stderr);
        }
        const std::string candidate_summary = SummarizeSyntaxError(candidate_error);
        const LuaOutputQualitySummary candidate_quality =
            SummarizeLuaOutputQuality(candidate_source);
        const bool same_family_quality_progress =
            IsSameFamilyQualityProgress(decompiled_source,
                                        baseline_validation_error,
                                        baseline_quality,
                                        candidate_source,
                                        candidate_error,
                                        candidate_quality);
        const bool candidate_acceptable =
            candidate_error.empty() ||
            (!baseline_validation_error.empty() &&
             ((candidate_error == baseline_validation_error && same_family_quality_progress) ||
              (candidate_summary == baseline_validation_summary &&
               same_family_quality_progress) ||
              IsValidationOutcomeBetter(baseline_validation_error, candidate_error)));
        if (!candidate_acceptable) {
            const DecompiledMarkerSummary candidate_marker_summary =
                SummarizeDecompilerMarkers(candidate_source);
            if (!IsDecompilerMarkerSummaryBetter(baseline_marker_summary,
                                                 candidate_marker_summary)) {
                return false;
            }
        }

        decompiled_source = std::move(candidate_source);
        lines = SplitLines(decompiled_source, &had_trailing_newline);
        if (note) {
            *note = JoinNotes(*note,
                              "native_function_assigned_boundary_replacement_prefix=" +
                                  assigned_prefix);
        }
        if (trace_function_replacement) {
            std::fprintf(stderr,
                         "[trace_replace] assigned_boundary_success prefix=%s\n",
                         assigned_prefix.c_str());
            std::fflush(stderr);
        }
        return true;
    };
    if (try_assigned_top_level_boundary_replacement()) {
        return true;
    }

    std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    const std::unordered_map<std::string, DecompiledFunctionSpan> prefix_span_map =
        BuildAssignedPrefixSpanMap(lines, spans);
    if (baseline_error_line != 0) {
        for (DecompiledFunctionSpan& span : spans) {
            const std::size_t span_start_line = span.start_line + 1;
            const std::size_t span_end_line = span.end_line + 1;
            if (baseline_error_line >= span_start_line &&
                baseline_error_line <= span_end_line + 2) {
                span.looks_suspicious = true;
                span.has_error = true;
            }
        }
    }

    std::vector<const refii::lua::Lua51Function*> functions;
    std::vector<std::size_t> function_root_child_indices;
    AppendLuaFunctionChildrenPreorderWithRootChildIndex(chunk.main,
                                                        0,
                                                        &functions,
                                                        &function_root_child_indices);
    if (spans.empty() || functions.empty()) {
        return false;
    }

    const auto span_targets_error_boundary = [&](const DecompiledFunctionSpan& span) {
        if (span.start_line > span.end_line || span.end_line >= lines.size()) {
            return false;
        }

        const std::size_t span_start_line = span.start_line + 1;
        const std::size_t span_end_line = span.end_line + 1;
        const auto matches_line = [&](std::size_t one_based_line) {
            if (one_based_line == 0) {
                return false;
            }
            return one_based_line + 1 >= span_start_line && one_based_line <= span_end_line + 2;
        };
        return matches_line(baseline_error_line) || matches_line(baseline_open_construct_line);
    };
    std::vector<DecompiledFunctionSpan> resolved_spans(functions.size());
    std::vector<bool> have_resolved_spans(functions.size(), false);
    for (std::size_t index = 0; index < functions.size(); ++index) {
        have_resolved_spans[index] =
            TryResolveFunctionSpanForIndex(chunk.main,
                                          index + 1,
                                          function_root_child_indices[index],
                                          lines,
                                          spans,
                                          &prefix_span_map,
                                          &resolved_spans[index]);
    }
    std::vector<FunctionReplacement> replacements;
    std::vector<FunctionReplacement> rendered_replacements;
    std::vector<FunctionReplacement> rendered_error_replacements;
    std::vector<FunctionReplacement> rendered_top_level_replacements;
    std::vector<FunctionReplacement> rendered_top_level_error_replacements;
    std::vector<FunctionReplacement> rendered_all_top_level_replacements;
    std::size_t suspicious_span_count = 0;
    std::unordered_map<std::size_t, bool> standalone_validation_cache;
    const bool consider_full_top_level_replacements = !baseline_validation_error.empty();
    const bool has_subclass_bootstrap_setlist_banner_artifact =
        baseline_error_line != 0 && baseline_error_line <= 128 &&
        decompiled_source.find("CreateSubClass(") != std::string::npos &&
        decompiled_source.find("MaintainedModes") != std::string::npos &&
        decompiled_source.find("No list found. Setlist fails") != std::string::npos &&
        decompiled_source.find("Overwrote pending register.") != std::string::npos;
    const bool has_subclass_bootstrap_boundary_artifact =
        baseline_error_line != 0 && baseline_error_line <= 128 &&
        decompiled_source.find(":CreateSubClass(") != std::string::npos &&
        decompiled_source.find(".MaintainedModes") != std::string::npos &&
        decompiled_source.find("-- refii-suppressed-control: end") != std::string::npos &&
        decompiled_source.find("= function(") != std::string::npos;
    const bool has_subclass_bootstrap_setlist_artifact =
        has_subclass_bootstrap_setlist_banner_artifact ||
        has_subclass_bootstrap_boundary_artifact;
    const std::size_t replacement_count = functions.size();
    bool has_targeted_error_boundary_span = false;
    if (baseline_error_line != 0) {
        for (std::size_t index = 0; index < resolved_spans.size(); ++index) {
            if (have_resolved_spans[index] &&
                span_targets_error_boundary(resolved_spans[index])) {
                has_targeted_error_boundary_span = true;
                break;
            }
        }
    }
    if (trace_function_replacement) {
        std::fprintf(stderr,
                     "[trace_replace] baseline_error_line=%zu open_construct_line=%zu "
                     "has_targeted=%d functions=%zu spans=%zu\n",
                     baseline_error_line,
                     baseline_open_construct_line,
                     has_targeted_error_boundary_span ? 1 : 0,
                     functions.size(),
                     spans.size());
        std::fflush(stderr);
    }
    const auto try_single_targeted_boundary_function_replacement = [&]() -> bool {
        if (!has_targeted_error_boundary_span) {
            return false;
        }

        struct TargetedBoundaryCandidate {
            std::size_t function_index = 0;
            DecompiledFunctionSpan span;
            std::size_t distance_to_boundary = 0;
        };

        std::vector<TargetedBoundaryCandidate> candidates;
        candidates.reserve(functions.size());
        for (std::size_t index = 0; index < functions.size(); ++index) {
            if (!have_resolved_spans[index] ||
                !span_targets_error_boundary(resolved_spans[index])) {
                continue;
            }
            const DecompiledFunctionSpan& span = resolved_spans[index];
            if (!span.top_level && !span.has_error && !span.looks_suspicious) {
                continue;
            }

            const std::size_t span_start_line = span.start_line + 1;
            const std::size_t span_end_line = span.end_line + 1;
            const auto distance_to_line = [&](std::size_t one_based_line) {
                if (one_based_line == 0) {
                    return std::numeric_limits<std::size_t>::max();
                }
                if (one_based_line < span_start_line) {
                    return span_start_line - one_based_line;
                }
                if (one_based_line > span_end_line + 2) {
                    return one_based_line - (span_end_line + 2);
                }
                return std::size_t{0};
            };

            candidates.push_back({index + 1,
                                  span,
                                  std::min(distance_to_line(baseline_open_construct_line),
                                           distance_to_line(baseline_error_line))});
        }

        if (candidates.empty()) {
            return false;
        }
        if (trace_function_replacement) {
            std::fprintf(stderr,
                         "[trace_replace] targeted_candidates=%zu\n",
                         candidates.size());
            std::fflush(stderr);
        }

        std::sort(candidates.begin(),
                  candidates.end(),
                  [](const TargetedBoundaryCandidate& lhs,
                     const TargetedBoundaryCandidate& rhs) {
                      if (lhs.distance_to_boundary != rhs.distance_to_boundary) {
                          return lhs.distance_to_boundary < rhs.distance_to_boundary;
                      }
                      if (lhs.span.top_level != rhs.span.top_level) {
                          return lhs.span.top_level;
                      }
                      if (lhs.span.has_error != rhs.span.has_error) {
                          return lhs.span.has_error;
                      }
                      if (lhs.span.looks_suspicious != rhs.span.looks_suspicious) {
                          return lhs.span.looks_suspicious;
                      }
                      const std::size_t lhs_span =
                          lhs.span.end_line >= lhs.span.start_line
                              ? (lhs.span.end_line - lhs.span.start_line)
                              : 0;
                      const std::size_t rhs_span =
                          rhs.span.end_line >= rhs.span.start_line
                              ? (rhs.span.end_line - rhs.span.start_line)
                              : 0;
                      if (lhs_span != rhs_span) {
                          return lhs_span < rhs_span;
                      }
                      return lhs.function_index < rhs.function_index;
                  });

        for (const TargetedBoundaryCandidate& candidate : candidates) {
            if (trace_function_replacement) {
                std::fprintf(stderr,
                             "[trace_replace] try_targeted_idx=%zu start=%zu end=%zu top=%d "
                             "err=%d suspicious=%d\n",
                             candidate.function_index,
                             candidate.span.start_line,
                             candidate.span.end_line,
                             candidate.span.top_level ? 1 : 0,
                             candidate.span.has_error ? 1 : 0,
                             candidate.span.looks_suspicious ? 1 : 0);
                std::fflush(stderr);
            }
            std::string rendered_function;
            std::string render_error;
            if (!refii::lua::RenderLua51StraightLineFunction(
                    *functions[candidate.function_index - 1],
                    &rendered_function,
                    &render_error)) {
                if (trace_function_replacement) {
                    std::fprintf(stderr,
                                 "[trace_replace] render_failed_idx=%zu err=%s\n",
                                 candidate.function_index,
                                 render_error.c_str());
                    std::fflush(stderr);
                }
                continue;
            }

            const LuaReadabilitySummary rendered_readability =
                SummarizeLuaReadability(rendered_function);
            if (ContainsObviouslyInvalidNilUsage(rendered_function)) {
                continue;
            }

            bool rendered_trailing_newline = false;
            std::vector<std::string> replacement_lines =
                SplitLines(rendered_function, &rendered_trailing_newline);
            if (replacement_lines.empty()) {
                continue;
            }
            if (replacement_lines.front().find("function") != std::string::npos &&
                !replacement_lines.front().starts_with(candidate.span.prefix)) {
                replacement_lines.front() = candidate.span.prefix + replacement_lines.front();
                replacement_lines.back() += candidate.span.suffix;
            }

            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(candidate.span.start_line),
                                  candidate_lines.begin() +
                                          static_cast<std::ptrdiff_t>(candidate.span.end_line) +
                                      1);
            candidate_lines.insert(candidate_lines.begin() +
                                       static_cast<std::ptrdiff_t>(candidate.span.start_line),
                                   replacement_lines.begin(),
                                   replacement_lines.end());

            std::string candidate_source = JoinLines(candidate_lines, had_trailing_newline);
            if (trace_function_replacement) {
                std::fprintf(stderr,
                             "[trace_replace] targeted_validate_candidate_idx=%zu\n",
                             candidate.function_index);
                std::fflush(stderr);
            }
            const std::string candidate_error =
                ValidateLuaSource(candidate_source, "native_function_replacement");
            const std::string candidate_summary = SummarizeSyntaxError(candidate_error);
            const LuaOutputQualitySummary candidate_quality =
                SummarizeLuaOutputQuality(candidate_source);
            const bool same_family_quality_progress =
                IsSameFamilyQualityProgress(decompiled_source,
                                            baseline_validation_error,
                                            baseline_quality,
                                            candidate_source,
                                            candidate_error,
                                            candidate_quality);
            const bool candidate_acceptable =
                candidate_error.empty() ||
                (!baseline_validation_error.empty() &&
                 ((candidate_error == baseline_validation_error &&
                   same_family_quality_progress) ||
                  (candidate_summary == baseline_validation_summary &&
                   same_family_quality_progress) ||
                  IsValidationOutcomeBetter(baseline_validation_error, candidate_error)));
            if (!candidate_acceptable) {
                const DecompiledMarkerSummary candidate_marker_summary =
                    SummarizeDecompilerMarkers(candidate_source);
                if (!IsDecompilerMarkerSummaryBetter(baseline_marker_summary,
                                                     candidate_marker_summary)) {
                    if (trace_function_replacement) {
                        std::fprintf(stderr,
                                     "[trace_replace] candidate_no_progress_idx=%zu err=%s\n",
                                     candidate.function_index,
                                     candidate_error.c_str());
                        std::fflush(stderr);
                    }
                    continue;
                }
            }

            decompiled_source = std::move(candidate_source);
            lines = SplitLines(decompiled_source, &had_trailing_newline);
            if (note) {
                *note = JoinNotes(*note,
                                  "native_function_targeted_boundary_single_idx=" +
                                      std::to_string(candidate.function_index));
            }
            if (trace_function_replacement) {
                std::fprintf(stderr,
                             "[trace_replace] targeted_success_idx=%zu\n",
                             candidate.function_index);
                std::fflush(stderr);
            }
            return true;
        }

        return false;
    };
    if (try_single_targeted_boundary_function_replacement()) {
        return true;
    }
    for (std::size_t index = 0; index < replacement_count; ++index) {
        if (!have_resolved_spans[index]) {
            continue;
        }
        const DecompiledFunctionSpan& span = resolved_spans[index];

        const bool targeted_span = span_targets_error_boundary(span);
        const bool named_parameter_mismatch =
            SpanUsesTemporaryParameterNamesWhenChunkHasNamedParameters(
                lines, span, *functions[index]);
        const bool consider_span =
            span.has_error || span.looks_suspicious ||
            (consider_full_top_level_replacements && span.top_level) ||
            named_parameter_mismatch;
        if (!consider_span) {
            continue;
        }
        if (has_targeted_error_boundary_span && !targeted_span &&
            !named_parameter_mismatch) {
            continue;
        }
        if (span.has_error || span.looks_suspicious || named_parameter_mismatch) {
            ++suspicious_span_count;
        }
        if (span.start_line > span.end_line || span.end_line >= lines.size()) {
            if (note) {
                *note = JoinNotes(*note,
                                  "native_function_span_invalid_idx=" +
                                      std::to_string(index + 1) + "@line=" +
                                      std::to_string(functions[index]->line_defined));
            }
            continue;
        }

        std::string rendered_function;
        std::string render_error;
        const bool rendered_ok =
            refii::lua::RenderLua51StraightLineFunction(*functions[index],
                                                        &rendered_function,
                                                        &render_error);
        if (!rendered_ok) {
            if (note && !render_error.empty()) {
                *note = JoinNotes(*note, "native_function_render_failed=" + render_error);
                *note = JoinNotes(*note,
                                  "native_function_render_failed_idx=" + std::to_string(index + 1) +
                                      "@line=" +
                                      std::to_string(functions[index]->line_defined));
            }
            continue;
        }

        bool rendered_function_trailing_newline = false;
        std::vector<std::string> rendered_function_lines =
            SplitLines(rendered_function, &rendered_function_trailing_newline);
        const LuaReadabilitySummary rendered_readability =
            SummarizeLuaReadability(rendered_function);
        const bool native_has_invalid_nil = ContainsObviouslyInvalidNilUsage(rendered_function);
        const bool native_has_nil_arithmetic =
            rendered_function.find("* nil") != std::string::npos ||
            rendered_function.find("nil *") != std::string::npos ||
            rendered_function.find("/ nil") != std::string::npos ||
            rendered_function.find("nil /") != std::string::npos;
        const bool native_quality_rejected = LooksLikeRegisterizedLua(rendered_readability);
        const bool native_structurally_rejected =
            !rendered_function_lines.empty() &&
            (LooksLikeBrokenMultiResultLocalSequence(rendered_function_lines,
                                                    0,
                                                    rendered_function_lines.size() - 1) ||
             LooksLikeDegenerateIfElseSequence(rendered_function_lines,
                                               0,
                                               rendered_function_lines.size() - 1));
        std::vector<std::string> replacement_lines;
        DecompiledFunctionSpan replacement_span = span;

        if (replacement_lines.empty()) {
            if (native_has_invalid_nil) {
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_invalid_nil_idx=" +
                                          std::to_string(index + 1));
                }
                continue;
            }
            if (native_structurally_rejected) {
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_structurally_rejected_idx=" +
                                          std::to_string(index + 1));
                }
            }
            if (native_quality_rejected) {
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_quality_rejected_idx=" +
                                          std::to_string(index + 1));
                    *note = JoinNotes(*note,
                                      "native_function_quality_rejected=" +
                                          DescribeLuaReadability(rendered_readability));
                }
                continue;
            }
            replacement_lines = rendered_function_lines;
        }

        if (replacement_lines.empty()) {
            continue;
        }
        if (replacement_lines.front().find("function") != std::string::npos &&
            !replacement_lines.front().starts_with(span.prefix)) {
            replacement_lines.front() = span.prefix + replacement_lines.front();
            replacement_lines.back() += span.suffix;
        }
        const std::string replacement_source = JoinLines(replacement_lines, true);
        std::vector<std::string> current_function_lines(
            lines.begin() + static_cast<std::ptrdiff_t>(replacement_span.start_line),
            lines.begin() + static_cast<std::ptrdiff_t>(replacement_span.end_line + 1));
        const bool replacement_exact_in_isolation =
            IsLuaFunctionSourceBytecodeExact(chunk, index + 1, replacement_lines);
        const bool replacement_improved_in_isolation =
            IsLuaFunctionSourceImprovedInIsolation(chunk,
                                                  index + 1,
                                                  current_function_lines,
                                                  replacement_lines);
        const bool replacement_validates_standalone =
            ValidateLuaSource(replacement_source, "native_function_replacement").empty();
        if (IsTopLevelAnonymousBootstrapSpan(replacement_span) &&
            (replacement_span.has_error || replacement_span.looks_suspicious)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "native_function_replacement_skipped_anonymous_top_level_idx=" +
                                      std::to_string(index + 1));
            }
            continue;
        }
        if (replacement_span.top_level &&
            (replacement_span.has_error || replacement_span.looks_suspicious) &&
            !replacement_exact_in_isolation && !replacement_improved_in_isolation &&
            !replacement_validates_standalone) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "native_function_replacement_deferred_nonimproving_top_level_idx=" +
                                      std::to_string(index + 1));
            }
        }

        int specificity_score = 0;
        if (span.top_level) {
            specificity_score += 5;
        }
        if (span.looks_suspicious) {
            specificity_score += 10;
        }
        if (span.has_error) {
            specificity_score += 20;
        }
        if (native_quality_rejected) {
            specificity_score += 30;
        }
        if (native_has_invalid_nil || native_has_nil_arithmetic) {
            specificity_score += 50;
        }

        rendered_replacements.push_back(
            {replacement_span.start_line,
             replacement_span.end_line,
             index + 1,
             replacement_span.top_level,
             replacement_span.looks_suspicious,
             replacement_span.has_error,
             replacement_exact_in_isolation,
             replacement_improved_in_isolation,
             specificity_score,
             replacement_lines});
        if (span.has_error) {
            rendered_error_replacements.push_back(
                {replacement_span.start_line,
                 replacement_span.end_line,
                 index + 1,
                 replacement_span.top_level,
                 replacement_span.looks_suspicious,
                 replacement_span.has_error,
                 replacement_exact_in_isolation,
                 replacement_improved_in_isolation,
                 specificity_score,
                 replacement_lines});
        }
        if (replacement_span.top_level) {
            rendered_all_top_level_replacements.push_back(
                {replacement_span.start_line,
                 replacement_span.end_line,
                 index + 1,
                 replacement_span.top_level,
                 replacement_span.looks_suspicious,
                 replacement_span.has_error,
                 replacement_exact_in_isolation,
                 replacement_improved_in_isolation,
                 specificity_score,
                 replacement_lines});
            rendered_top_level_replacements.push_back(
                {replacement_span.start_line,
                 replacement_span.end_line,
                 index + 1,
                 replacement_span.top_level,
                 replacement_span.looks_suspicious,
                 replacement_span.has_error,
                 replacement_exact_in_isolation,
                 replacement_improved_in_isolation,
                 specificity_score,
                 replacement_lines});
            if (replacement_span.has_error) {
                rendered_top_level_error_replacements.push_back(
                    {replacement_span.start_line,
                     replacement_span.end_line,
                     index + 1,
                     replacement_span.top_level,
                     replacement_span.looks_suspicious,
                     replacement_span.has_error,
                     replacement_exact_in_isolation,
                     replacement_improved_in_isolation,
                     specificity_score,
                     replacement_lines});
            }
        }
    }

    if (has_subclass_bootstrap_setlist_artifact) {
        rendered_top_level_replacements.clear();
        rendered_top_level_error_replacements.clear();
        rendered_all_top_level_replacements.clear();
        if (note) {
            *note = JoinNotes(*note,
                              "native_function_top_level_replacements_skipped_subclass_bootstrap");
        }
    }
    if (baseline_reports_nested_function_boundary) {
        const auto retain_safe_nested_boundary_top_level_replacements =
            [](std::vector<FunctionReplacement>* replacements) {
                if (replacements == nullptr || replacements->empty()) {
                    return;
                }

                replacements->erase(
                    std::remove_if(replacements->begin(),
                                   replacements->end(),
                                   [](const FunctionReplacement& replacement) {
                                       if (!replacement.top_level) {
                                           return true;
                                       }
                                       if (!replacement.exact_in_isolation &&
                                           !replacement.improved_in_isolation) {
                                           return true;
                                       }
                                       const std::string replacement_source =
                                           JoinLines(replacement.lines, true);
                                       return !ValidateLuaSource(
                                                   replacement_source,
                                                   "native_function_replacement")
                                                   .empty();
                                   }),
                    replacements->end());
            };
        const std::size_t original_top_level_count =
            rendered_top_level_replacements.size();
        const std::size_t original_top_level_error_count =
            rendered_top_level_error_replacements.size();
        const std::size_t original_all_top_level_count =
            rendered_all_top_level_replacements.size();
        retain_safe_nested_boundary_top_level_replacements(
            &rendered_top_level_replacements);
        retain_safe_nested_boundary_top_level_replacements(
            &rendered_top_level_error_replacements);
        retain_safe_nested_boundary_top_level_replacements(
            &rendered_all_top_level_replacements);
        if (note) {
            if (rendered_top_level_replacements.empty() &&
                rendered_top_level_error_replacements.empty() &&
                rendered_all_top_level_replacements.empty()) {
                *note = JoinNotes(
                    *note,
                    "native_function_top_level_replacements_skipped_nested_function_boundary");
            } else {
                *note = JoinNotes(
                    *note,
                    "native_function_top_level_replacements_limited_nested_function_boundary=" +
                        std::to_string(rendered_top_level_replacements.size()) + "/" +
                        std::to_string(original_top_level_count) + "," +
                        std::to_string(rendered_top_level_error_replacements.size()) + "/" +
                        std::to_string(original_top_level_error_count) + "," +
                        std::to_string(rendered_all_top_level_replacements.size()) + "/" +
                        std::to_string(original_all_top_level_count));
            }
        }
    }

    if (rendered_replacements.empty()) {
        return false;
    }

    auto try_targeted_boundary_replacement =
        [&](const std::vector<FunctionReplacement>& candidates,
            std::string_view note_label) -> bool {
        if (!has_targeted_error_boundary_span || candidates.empty()) {
            return false;
        }

        std::vector<FunctionReplacement> targeted_candidates;
        targeted_candidates.reserve(candidates.size());
        for (const FunctionReplacement& replacement : candidates) {
            const DecompiledFunctionSpan replacement_span{
                replacement.start_line,
                replacement.end_line,
                {},
                {},
                replacement.has_error,
                replacement.looks_suspicious,
                replacement.top_level,
            };
            if (!span_targets_error_boundary(replacement_span)) {
                continue;
            }
            const std::string replacement_source = JoinLines(replacement.lines, true);
            if (!ValidateLuaSource(replacement_source, "native_function_replacement").empty()) {
                continue;
            }
            targeted_candidates.push_back(replacement);
        }
        if (targeted_candidates.empty()) {
            return false;
        }

        std::sort(targeted_candidates.begin(),
                  targeted_candidates.end(),
                  [&](const FunctionReplacement& lhs, const FunctionReplacement& rhs) {
                      const auto distance_to_boundary = [&](const FunctionReplacement& replacement) {
                          const std::size_t start_line = replacement.start_line + 1;
                          const std::size_t end_line = replacement.end_line + 1;
                          const auto distance_to_line = [&](std::size_t one_based_line) {
                              if (one_based_line == 0) {
                                  return std::numeric_limits<std::size_t>::max();
                              }
                              if (one_based_line < start_line) {
                                  return start_line - one_based_line;
                              }
                              if (one_based_line > end_line + 2) {
                                  return one_based_line - (end_line + 2);
                              }
                              return std::size_t{0};
                          };
                          return std::min(distance_to_line(baseline_open_construct_line),
                                          distance_to_line(baseline_error_line));
                      };

                      const std::size_t lhs_distance = distance_to_boundary(lhs);
                      const std::size_t rhs_distance = distance_to_boundary(rhs);
                      if (lhs_distance != rhs_distance) {
                          return lhs_distance < rhs_distance;
                      }
                      if (lhs.has_error != rhs.has_error) {
                          return lhs.has_error;
                      }
                      if (lhs.top_level != rhs.top_level) {
                          return lhs.top_level;
                      }
                      if (lhs.looks_suspicious != rhs.looks_suspicious) {
                          return lhs.looks_suspicious;
                      }
                      if (lhs.specificity_score != rhs.specificity_score) {
                          return lhs.specificity_score > rhs.specificity_score;
                      }
                      return lhs.function_index < rhs.function_index;
                  });

        for (const FunctionReplacement& replacement : targeted_candidates) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(replacement.start_line),
                                  candidate_lines.begin() +
                                          static_cast<std::ptrdiff_t>(replacement.end_line) +
                                      1);
            candidate_lines.insert(candidate_lines.begin() +
                                       static_cast<std::ptrdiff_t>(replacement.start_line),
                                   replacement.lines.begin(),
                                   replacement.lines.end());

            std::string candidate_source = JoinLines(candidate_lines, had_trailing_newline);
            std::string candidate_error =
                ValidateLuaSource(candidate_source, "native_function_replacement");
            const std::string candidate_summary = SummarizeSyntaxError(candidate_error);
            const LuaOutputQualitySummary candidate_quality =
                SummarizeLuaOutputQuality(candidate_source);
            const bool same_family_quality_progress =
                IsSameFamilyQualityProgress(decompiled_source,
                                            baseline_validation_error,
                                            baseline_quality,
                                            candidate_source,
                                            candidate_error,
                                            candidate_quality);
            const bool candidate_acceptable =
                candidate_error.empty() ||
                (!baseline_validation_error.empty() &&
                 ((candidate_error == baseline_validation_error &&
                   same_family_quality_progress) ||
                  (candidate_summary == baseline_validation_summary &&
                   same_family_quality_progress) ||
                  IsValidationOutcomeBetter(baseline_validation_error, candidate_error)));
            if (!candidate_acceptable) {
                const DecompiledMarkerSummary candidate_marker_summary =
                    SummarizeDecompilerMarkers(candidate_source);
                if (!IsDecompilerMarkerSummaryBetter(baseline_marker_summary,
                                                     candidate_marker_summary)) {
                    continue;
                }
            }

            decompiled_source = std::move(candidate_source);
            lines = SplitLines(decompiled_source, &had_trailing_newline);
            if (note) {
                *note = JoinNotes(*note,
                                  std::string(note_label) + "_idx=" +
                                      std::to_string(replacement.function_index));
            }
            return true;
        }

        return false;
    };

    if (try_targeted_boundary_replacement(rendered_replacements,
                                          "native_function_targeted_boundary_replacement")) {
        return true;
    }

    auto sort_replacements = [](std::vector<FunctionReplacement>* replacement_list) {
        std::sort(replacement_list->begin(), replacement_list->end(),
                  [](const FunctionReplacement& lhs, const FunctionReplacement& rhs) {
                      return lhs.start_line > rhs.start_line;
                  });
    };

    auto prune_nested_replacements_prefer_specific =
        [](std::vector<FunctionReplacement>* replacement_list) {
        if (replacement_list == nullptr || replacement_list->size() < 2) {
            return;
        }

        const auto replacement_better = [](const FunctionReplacement& lhs,
                                           const FunctionReplacement& rhs) {
            if (lhs.specificity_score != rhs.specificity_score) {
                return lhs.specificity_score > rhs.specificity_score;
            }
            if (lhs.has_error != rhs.has_error) {
                return lhs.has_error;
            }
            if (lhs.looks_suspicious != rhs.looks_suspicious) {
                return lhs.looks_suspicious;
            }
            const std::size_t lhs_span =
                lhs.end_line >= lhs.start_line ? (lhs.end_line - lhs.start_line) : 0;
            const std::size_t rhs_span =
                rhs.end_line >= rhs.start_line ? (rhs.end_line - rhs.start_line) : 0;
            if (lhs_span != rhs_span) {
                return lhs_span < rhs_span;
            }
            return lhs.function_index < rhs.function_index;
        };

        std::sort(replacement_list->begin(), replacement_list->end(),
                  [](const FunctionReplacement& lhs, const FunctionReplacement& rhs) {
                      if (lhs.start_line != rhs.start_line) {
                          return lhs.start_line < rhs.start_line;
                      }
                      if (lhs.end_line != rhs.end_line) {
                          return lhs.end_line > rhs.end_line;
                      }
                      return lhs.function_index < rhs.function_index;
                  });

        std::vector<FunctionReplacement> kept;
        kept.reserve(replacement_list->size());
        for (const FunctionReplacement& replacement : *replacement_list) {
            bool skip_replacement = false;
            for (auto existing = kept.begin(); existing != kept.end();) {
                const bool existing_contains_replacement =
                    existing->start_line <= replacement.start_line &&
                    existing->end_line >= replacement.end_line;
                if (existing_contains_replacement) {
                    if (!replacement_better(replacement, *existing)) {
                        skip_replacement = true;
                        break;
                    }
                    existing = kept.erase(existing);
                    continue;
                }

                const bool replacement_contains_existing =
                    replacement.start_line <= existing->start_line &&
                    replacement.end_line >= existing->end_line;
                if (replacement_contains_existing) {
                    if (!replacement_better(replacement, *existing)) {
                        skip_replacement = true;
                        break;
                    }
                    existing = kept.erase(existing);
                    continue;
                }

                ++existing;
            }

            if (!skip_replacement) {
                kept.push_back(replacement);
            }
        }

        *replacement_list = std::move(kept);
    };

    auto apply_replacements = [&](const std::vector<FunctionReplacement>& replacement_list,
                                  std::vector<std::string>* target_lines,
                                  bool record_notes) {
        std::size_t applied_floor = target_lines->size();
        std::size_t applied_count = 0;
        for (const FunctionReplacement& replacement : replacement_list) {
            if (replacement.start_line > replacement.end_line ||
                replacement.end_line >= target_lines->size() ||
                replacement.end_line >= applied_floor) {
                if (record_notes && note) {
                    *note = JoinNotes(*note,
                                      "native_function_replacement_skipped_range=" +
                                          std::to_string(replacement.start_line) + "-" +
                                          std::to_string(replacement.end_line));
                }
                continue;
            }
            target_lines->erase(
                target_lines->begin() + static_cast<std::ptrdiff_t>(replacement.start_line),
                target_lines->begin() + static_cast<std::ptrdiff_t>(replacement.end_line) + 1);
            target_lines->insert(
                target_lines->begin() + static_cast<std::ptrdiff_t>(replacement.start_line),
                replacement.lines.begin(),
                replacement.lines.end());
            applied_floor = replacement.start_line;
            ++applied_count;
            if (record_notes && note) {
                *note = JoinNotes(*note,
                                  "native_function_replacement_applied_idx=" +
                                      std::to_string(replacement.function_index));
            }
        }
        return applied_count;
    };

    auto try_combined_replacements =
        [&](std::vector<FunctionReplacement> replacement_list,
            std::string_view combined_note_label) -> bool {
        if (replacement_list.empty()) {
            return false;
        }

        prune_nested_replacements_prefer_specific(&replacement_list);
        sort_replacements(&replacement_list);
        std::vector<std::string> combined_lines = lines;
        const std::size_t combined_applied =
            apply_replacements(replacement_list, &combined_lines, false);
        if (combined_applied == 0) {
            return false;
        }

        std::string combined_source = JoinLines(combined_lines, had_trailing_newline);
        const LuaReadabilitySummary combined_readability =
            SummarizeLuaReadability(combined_source);
        bool combined_has_registerized_replacement = false;
        for (const FunctionReplacement& replacement : replacement_list) {
            const std::string replacement_source = JoinLines(replacement.lines, true);
            const LuaReadabilitySummary replacement_readability =
                SummarizeLuaReadability(replacement_source);
            if (LooksLikeRegisterizedLua(replacement_readability)) {
                combined_has_registerized_replacement = true;
                break;
            }
        }
        if (LooksLikeRegisterizedLua(combined_readability) &&
            combined_has_registerized_replacement) {
            if (note) {
                *note = JoinNotes(*note,
                                  std::string(combined_note_label) + "_quality_rejected=" +
                                      DescribeLuaReadability(combined_readability));
            }
            return false;
        }

        std::string combined_error =
            ValidateLuaSource(combined_source, "native_function_replacement");
        TryRepairNativeCandidateSource(combined_source,
                                       nullptr,
                                       note,
                                       combined_note_label,
                                       &combined_error);
        const std::string combined_summary = SummarizeSyntaxError(combined_error);
        const LuaOutputQualitySummary combined_quality =
            SummarizeLuaOutputQuality(combined_source);
        const bool same_family_quality_progress =
            IsSameFamilyQualityProgress(decompiled_source,
                                        baseline_validation_error,
                                        baseline_quality,
                                        combined_source,
                                        combined_error,
                                        combined_quality);
        const bool combined_acceptable =
            combined_error.empty() ||
            (!baseline_validation_error.empty() &&
             ((combined_error == baseline_validation_error && same_family_quality_progress) ||
              (combined_summary == baseline_validation_summary &&
               same_family_quality_progress) ||
              IsValidationOutcomeBetter(baseline_validation_error, combined_error)));
        if (!combined_acceptable) {
            const DecompiledMarkerSummary combined_marker_summary =
                SummarizeDecompilerMarkers(combined_source);
            if (IsDecompilerMarkerSummaryBetter(baseline_marker_summary,
                                                combined_marker_summary)) {
                decompiled_source = std::move(combined_source);
                lines = SplitLines(decompiled_source, &had_trailing_newline);
                if (note) {
                    *note = JoinNotes(*note,
                                      std::string(combined_note_label) +
                                          "_progress=" + std::to_string(combined_applied));
                    *note = JoinNotes(
                        *note,
                        std::string(combined_note_label) + "_progress_markers=" +
                            std::to_string(combined_marker_summary.decompiler_error_count) + "," +
                            std::to_string(combined_marker_summary.repeated_control_flow_count) +
                            "," +
                            std::to_string(combined_marker_summary.suspicious_while_true_count +
                                           combined_marker_summary.suspicious_until_true_count +
                                           combined_marker_summary.for_generator_count));
                }
                return true;
            }
            if (note) {
                *note = JoinNotes(*note,
                                  std::string(combined_note_label) + "_invalid=" +
                                      SummarizeSyntaxError(combined_error));
            }
            return false;
        }

        decompiled_source = std::move(combined_source);
        lines = SplitLines(decompiled_source, &had_trailing_newline);
        if (note) {
            *note = JoinNotes(*note,
                              std::string(combined_note_label) + "=" +
                                  std::to_string(combined_applied));
            for (const FunctionReplacement& replacement : replacement_list) {
                *note = JoinNotes(*note,
                                  "native_function_replacement_applied_idx=" +
                                      std::to_string(replacement.function_index));
            }
            *note = JoinNotes(*note,
                              "native_function_replacements=" +
                                  std::to_string(combined_applied));
        }
        return true;
    };

    const bool prefer_combined_replacement =
        baseline_has_repeated_control_flow || suspicious_span_count >= 24;
    const bool restrict_to_explicit_errors = !rendered_error_replacements.empty();
    std::vector<FunctionReplacement> precise_error_replacements;
    precise_error_replacements.reserve(rendered_error_replacements.size());
    for (const FunctionReplacement& replacement : rendered_error_replacements) {
        if (replacement.specificity_score >= 100) {
            precise_error_replacements.push_back(replacement);
        }
    }
    std::vector<FunctionReplacement> bytecode_proven_error_replacements;
    bytecode_proven_error_replacements.reserve(rendered_error_replacements.size());
    for (const FunctionReplacement& replacement : rendered_error_replacements) {
        if (replacement.exact_in_isolation || replacement.improved_in_isolation) {
            bytecode_proven_error_replacements.push_back(replacement);
        }
    }
    std::vector<FunctionReplacement> bytecode_proven_replacements;
    bytecode_proven_replacements.reserve(rendered_replacements.size());
    for (const FunctionReplacement& replacement : rendered_replacements) {
        if (replacement.exact_in_isolation || replacement.improved_in_isolation) {
            bytecode_proven_replacements.push_back(replacement);
        }
    }
    std::vector<FunctionReplacement> bytecode_proven_top_level_replacements;
    bytecode_proven_top_level_replacements.reserve(rendered_top_level_replacements.size());
    for (const FunctionReplacement& replacement : rendered_top_level_replacements) {
        if (replacement.exact_in_isolation || replacement.improved_in_isolation) {
            bytecode_proven_top_level_replacements.push_back(replacement);
        }
    }
    if (!precise_error_replacements.empty() &&
        try_combined_replacements(precise_error_replacements,
                                  "native_function_precise_error_replacements")) {
        return true;
    }
    if (!bytecode_proven_error_replacements.empty() &&
        try_combined_replacements(bytecode_proven_error_replacements,
                                  "native_function_bytecode_proven_error_replacements")) {
        return true;
    }
    if (!bytecode_proven_replacements.empty() &&
        try_combined_replacements(bytecode_proven_replacements,
                                  "native_function_bytecode_proven_replacements")) {
        return true;
    }

    if (!rendered_top_level_error_replacements.empty() &&
        try_combined_replacements(rendered_top_level_error_replacements,
                                  "native_function_top_level_error_replacements")) {
        return true;
    }
    if (!rendered_error_replacements.empty() &&
        try_combined_replacements(rendered_error_replacements,
                                  "native_function_error_replacements")) {
        return true;
    }

    if (!restrict_to_explicit_errors && !rendered_top_level_replacements.empty() &&
        try_combined_replacements(rendered_top_level_replacements,
                                  "native_function_top_level_combined_replacements")) {
        return true;
    }

    if (consider_full_top_level_replacements &&
        !rendered_all_top_level_replacements.empty() &&
        try_combined_replacements(rendered_all_top_level_replacements,
                                  "native_function_top_level_full_replacements")) {
        return true;
    }

    if (!restrict_to_explicit_errors &&
        (prefer_combined_replacement || rendered_replacements.size() > 1) &&
        try_combined_replacements(rendered_replacements,
                                  "native_function_combined_replacements")) {
        return true;
    }

    auto collect_validation_candidates = [&](const std::vector<FunctionReplacement>& candidates) {
        const std::size_t baseline_invalid_nil_count =
            CountObviouslyInvalidNilUsage(decompiled_source);
        const auto is_already_selected = [&](const FunctionReplacement& replacement) {
            return std::any_of(replacements.begin(),
                               replacements.end(),
                               [&](const FunctionReplacement& existing) {
                                   return existing.start_line == replacement.start_line &&
                                          existing.end_line == replacement.end_line &&
                                          existing.function_index == replacement.function_index;
                               });
        };

        const auto replacement_validates_standalone =
            [&](const FunctionReplacement& replacement) {
                const auto cached = standalone_validation_cache.find(replacement.function_index);
                if (cached != standalone_validation_cache.end()) {
                    return cached->second;
                }

                std::string replacement_source = JoinLines(replacement.lines, true);
                const bool valid =
                    ValidateLuaSource(replacement_source, "native_function_replacement") == "";
                standalone_validation_cache.emplace(replacement.function_index, valid);
                return valid;
            };

        for (const FunctionReplacement& replacement : candidates) {
            const DecompiledFunctionSpan replacement_span{
                replacement.start_line,
                replacement.end_line,
                {},
                {},
                replacement.has_error,
                replacement.looks_suspicious,
                replacement.top_level,
            };
            if (has_targeted_error_boundary_span &&
                !span_targets_error_boundary(replacement_span)) {
                continue;
            }
            if (has_subclass_bootstrap_setlist_artifact && replacement.top_level) {
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_replacement_skipped_subclass_bootstrap_idx=" +
                                          std::to_string(replacement.function_index));
                }
                continue;
            }
            if (baseline_reports_nested_function_boundary && replacement.top_level &&
                replacement.looks_suspicious) {
                if (note) {
                    *note = JoinNotes(
                        *note,
                        "native_function_replacement_skipped_nested_function_boundary_idx=" +
                            std::to_string(replacement.function_index));
                }
                continue;
            }

            const bool replacement_is_bytecode_proven =
                replacement.exact_in_isolation || replacement.improved_in_isolation;
            const bool standalone_valid = replacement_validates_standalone(replacement);
            const bool standalone_bytecode_top_level =
                replacement.top_level && replacement_is_bytecode_proven && standalone_valid;
            const bool late_top_level_standalone_candidate =
                replacement.top_level &&
                standalone_valid &&
                baseline_has_unclosed_construct_error &&
                baseline_open_construct_line != 0 &&
                replacement.end_line + 1 >= baseline_open_construct_line;
            if ((replacement.looks_suspicious &&
                 standalone_valid &&
                 (replacement.top_level || replacement_is_bytecode_proven)) ||
                standalone_bytecode_top_level ||
                late_top_level_standalone_candidate) {
                if (!is_already_selected(replacement)) {
                    replacements.push_back(replacement);
                }
                if (note) {
                    *note = JoinNotes(
                        *note,
                        std::string(
                            standalone_bytecode_top_level
                                ? "native_function_candidate_bytecode_top_level_standalone_selected_idx="
                                : (late_top_level_standalone_candidate
                                       ? "native_function_candidate_late_top_level_standalone_selected_idx="
                                       : (replacement.top_level
                                              ? "native_function_candidate_standalone_selected_idx="
                                              : "native_function_candidate_bytecode_standalone_selected_idx="))) +
                            std::to_string(replacement.function_index));
                }
                continue;
            }

            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(replacement.start_line),
                                  candidate_lines.begin() +
                                          static_cast<std::ptrdiff_t>(replacement.end_line) +
                                      1);
            candidate_lines.insert(candidate_lines.begin() +
                                       static_cast<std::ptrdiff_t>(replacement.start_line),
                                   replacement.lines.begin(),
                                   replacement.lines.end());

            const std::string candidate_source = JoinLines(candidate_lines, had_trailing_newline);
            const LuaReadabilitySummary candidate_readability =
                SummarizeLuaReadability(candidate_source);
            const std::string replacement_source = JoinLines(replacement.lines, true);
            const LuaReadabilitySummary replacement_readability =
                SummarizeLuaReadability(replacement_source);
            const std::size_t replacement_invalid_nil_count =
                CountObviouslyInvalidNilUsage(replacement_source);
            const std::size_t candidate_invalid_nil_count =
                CountObviouslyInvalidNilUsage(candidate_source);
            if (replacement_invalid_nil_count != 0 ||
                candidate_invalid_nil_count > baseline_invalid_nil_count) {
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_candidate_invalid_nil_idx=" +
                                          std::to_string(replacement.function_index));
                }
                continue;
            }
            if (LooksLikeRegisterizedLua(candidate_readability) &&
                LooksLikeRegisterizedLua(replacement_readability)) {
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_candidate_quality_rejected_idx=" +
                                          std::to_string(replacement.function_index));
                    *note = JoinNotes(*note,
                                      "native_function_candidate_quality_rejected=" +
                                          DescribeLuaReadability(candidate_readability));
                }
                continue;
            }

            const std::string candidate_error =
                ValidateLuaSource(candidate_source, "native_function_replacement");
            const std::string candidate_summary = SummarizeSyntaxError(candidate_error);
            const LuaOutputQualitySummary candidate_quality =
                SummarizeLuaOutputQuality(candidate_source);
            const bool same_family_quality_progress =
                IsSameFamilyQualityProgress(decompiled_source,
                                            baseline_validation_error,
                                            baseline_quality,
                                            candidate_source,
                                            candidate_error,
                                            candidate_quality);
            const bool candidate_acceptable =
                candidate_error.empty() ||
                (!baseline_validation_error.empty() &&
                 ((candidate_error == baseline_validation_error &&
                   same_family_quality_progress) ||
                  (candidate_summary == baseline_validation_summary &&
                   same_family_quality_progress) ||
                  IsValidationOutcomeBetter(baseline_validation_error, candidate_error)));
            if (!candidate_acceptable) {
                const bool bytecode_proven_progress =
                    replacement.top_level &&
                    replacement_is_bytecode_proven &&
                    standalone_valid &&
                    IsBytecodeProvenValidationProgress(baseline_validation_error,
                                                       candidate_error,
                                                       baseline_quality,
                                                       candidate_quality);
                if (bytecode_proven_progress) {
                    if (!is_already_selected(replacement)) {
                        replacements.push_back(replacement);
                    }
                    if (note) {
                        *note = JoinNotes(*note,
                                          "native_function_candidate_bytecode_progress_idx=" +
                                              std::to_string(replacement.function_index));
                    }
                    continue;
                }
                const DecompiledMarkerSummary candidate_marker_summary =
                    SummarizeDecompilerMarkers(candidate_source);
                if (IsDecompilerMarkerSummaryBetter(baseline_marker_summary,
                                                    candidate_marker_summary)) {
                    if (!is_already_selected(replacement)) {
                        replacements.push_back(replacement);
                    }
                    if (note) {
                        *note = JoinNotes(*note,
                                          "native_function_candidate_progress_idx=" +
                                              std::to_string(replacement.function_index));
                    }
                    continue;
                }
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_candidate_invalid=" +
                                          SummarizeSyntaxError(candidate_error));
                    *note = JoinNotes(*note,
                                      "native_function_candidate_invalid_idx=" +
                                          std::to_string(replacement.function_index));
                }
                continue;
            }

            if (!is_already_selected(replacement)) {
                replacements.push_back(replacement);
            }
        }
    };

    if (!rendered_top_level_error_replacements.empty()) {
        sort_replacements(&rendered_top_level_error_replacements);
        collect_validation_candidates(rendered_top_level_error_replacements);
    }
    if (replacements.empty() && !rendered_error_replacements.empty()) {
        sort_replacements(&rendered_error_replacements);
        collect_validation_candidates(rendered_error_replacements);
    }
    if (!bytecode_proven_top_level_replacements.empty()) {
        sort_replacements(&bytecode_proven_top_level_replacements);
        collect_validation_candidates(bytecode_proven_top_level_replacements);
    }
    if (!restrict_to_explicit_errors && replacements.empty() && !rendered_top_level_replacements.empty()) {
        sort_replacements(&rendered_top_level_replacements);
        collect_validation_candidates(rendered_top_level_replacements);
    }
    if (consider_full_top_level_replacements &&
        !rendered_all_top_level_replacements.empty()) {
        sort_replacements(&rendered_all_top_level_replacements);
        collect_validation_candidates(rendered_all_top_level_replacements);
    }
    if (!restrict_to_explicit_errors && replacements.empty() &&
        rendered_top_level_replacements.size() != rendered_replacements.size()) {
        sort_replacements(&rendered_replacements);
        collect_validation_candidates(rendered_replacements);
    }

    if (replacements.empty()) {
        return false;
    }

    prune_nested_replacements_prefer_specific(&replacements);
    sort_replacements(&replacements);

    const std::size_t applied_count = apply_replacements(replacements, &lines, true);

    decompiled_source = JoinLines(lines, had_trailing_newline);
    const LuaReadabilitySummary replacement_readability =
        SummarizeLuaReadability(decompiled_source);
    if (LooksLikeRegisterizedLua(replacement_readability) &&
        !LooksLikeRegisterizedLua(baseline_readability_summary) &&
        !IsStructuredTableInitializerLuaDespiteTemporaryNoise(replacement_readability)) {
        if (note) {
            *note = JoinNotes(*note,
                              "native_function_replacements_quality_rejected=" +
                                  DescribeLuaReadability(replacement_readability));
        }
        return false;
    }
    if (note) {
        *note = JoinNotes(*note,
                          "native_function_replacements=" +
                              std::to_string(applied_count));
    }
    return applied_count != 0;
}

bool TryReplaceWithValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                            std::string& decompiled_source,
                                            std::string* note,
                                            const refii::lua::LuaWorkspaceDecompileOptions* options = nullptr,
                                            const std::filesystem::path* input_path = nullptr,
                                            std::string_view preferred_render_mode = {});
std::filesystem::path MakeNativeMainChunkTempOutputPath(const std::filesystem::path& input_path);
bool ReadTextFile(const std::filesystem::path& path, std::string& text);
bool WriteTextFile(const std::filesystem::path& path, std::string_view text);
void MaybeDumpInvalidNativeCandidate(std::string_view note_prefix,
                                     const std::filesystem::path* input_path,
                                     std::string_view source,
                                     std::string_view validation_error);

#include "decompiler/rendered/pipelines/native_main_chunk_policy_helpers.inc"

#include "decompiler/rendered/pipelines/native_main_chunk_render_pipeline.inc"

#include "decompiler/rendered/pipelines/native_render_tool_io_helpers.inc"

bool ShouldRequireExactParityForCanonicalDebugBank(
    const std::filesystem::path& relative_input,
    const std::filesystem::path* input_path,
    const std::vector<char>& input_bytes) {
    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }
    if (ShouldPreferNativeFirstForCanonicalDebugBank(relative_input, input_bytes)) {
        return true;
    }
    if (input_path == nullptr) {
        return false;
    }

    const std::string normalized_input_path =
        NormalizePathString(input_path->generic_string());
    return normalized_input_path.find("/.source/gamescripts/scripts/") != std::string::npos ||
           normalized_input_path.find("/.source/scripts/") != std::string::npos;
}

bool ShouldRequireExactParityForInputPolicy(
    const std::filesystem::path& relative_input,
    const std::filesystem::path* input_path,
    const std::vector<char>& input_bytes,
    const refii::lua::LuaWorkspaceDecompileOptions* options) {
    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    const refii::lua::LuaExactParityPolicy policy =
        options == nullptr ? refii::lua::LuaExactParityPolicy::kCanonicalOnly
                           : options->exact_parity_policy;
    if (policy == refii::lua::LuaExactParityPolicy::kAlwaysForBytecode) {
        return true;
    }

    return ShouldRequireExactParityForCanonicalDebugBank(
        relative_input, input_path, input_bytes);
}

bool TryEvaluateExactParityCandidate(const refii::lua::audit::LuaBytecodeParityReference& reference,
                                     std::string_view source,
                                     std::string_view chunk_name,
                                     refii::lua::audit::LuaBytecodeParityResult* parity_result) {
    if (parity_result == nullptr) {
        return false;
    }

    *parity_result = {};
    return refii::lua::audit::EvaluateLuaSourceBytecodeParity(
               reference, source, chunk_name, parity_result) &&
           parity_result->exact_bytecode;
}

void AppendParityClassificationNote(std::string* note,
                                    std::string_view note_prefix,
                                    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    if (note == nullptr || note_prefix.empty()) {
        return;
    }

    if (!parity_result.error.empty()) {
        *note = JoinNotes(*note,
                          std::string(note_prefix) + "_parity_compile_failed=" +
                              SummarizeSyntaxError(parity_result.error));
        return;
    }

    if (parity_result.first_mismatch.available) {
        *note = JoinNotes(*note,
                          std::string(note_prefix) + "_parity_mismatch=" +
                              refii::lua::audit::DescribeMismatchForSummary(
                                  parity_result.first_mismatch));
        return;
    }

    *note = JoinNotes(*note, std::string(note_prefix) + "_parity_nonexact");
}

bool TryStripSyntheticRootWrapperSource(std::string* source, std::string* note) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    bool had_trailing_newline = !source->empty() && source->back() == '\n';
    std::vector<std::string> lines = refii::lua::SplitRenderedLines(*source);
    if (lines.empty()) {
        return false;
    }

    std::size_t first_nonblank = 0;
    while (first_nonblank < lines.size() &&
           refii::lua::TrimLuaWhitespace(
               refii::lua::StripLuaLineComment(lines[first_nonblank]))
               .empty()) {
        ++first_nonblank;
    }
    if (first_nonblank >= lines.size()) {
        return false;
    }

    std::size_t last_nonblank = lines.size() - 1;
    while (last_nonblank > first_nonblank &&
           refii::lua::TrimLuaWhitespace(
               refii::lua::StripLuaLineComment(lines[last_nonblank]))
               .empty()) {
        --last_nonblank;
    }

    const std::string_view header =
        refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(lines[first_nonblank]));
    const std::string_view footer =
        refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(lines[last_nonblank]));
    if (!header.starts_with("function(") || footer != "end") {
        return false;
    }

    for (std::size_t probe = 0; probe < first_nonblank; ++probe) {
        if (!refii::lua::TrimLuaWhitespace(
                 refii::lua::StripLuaLineComment(lines[probe]))
                 .empty()) {
            return false;
        }
    }
    for (std::size_t probe = last_nonblank + 1; probe < lines.size(); ++probe) {
        if (!refii::lua::TrimLuaWhitespace(
                 refii::lua::StripLuaLineComment(lines[probe]))
                 .empty()) {
            return false;
        }
    }

    lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(last_nonblank));
    lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(first_nonblank));

    bool normalized_module_vararg_seed = false;
    if (!lines.empty()) {
        std::size_t first_content = 0;
        while (first_content < lines.size() &&
               refii::lua::TrimLuaWhitespace(
                   refii::lua::StripLuaLineComment(lines[first_content]))
                   .empty()) {
            ++first_content;
        }
        if (first_content < lines.size()) {
            const std::string_view first_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment(lines[first_content]));
            if (first_trimmed.starts_with("local ")) {
                const std::size_t assign_pos = first_trimmed.find('=');
                if (assign_pos != std::string_view::npos) {
                    const std::string_view local_name =
                        refii::lua::TrimLuaWhitespace(
                            first_trimmed.substr(6, assign_pos - 6));
                    const std::string_view rhs =
                        refii::lua::TrimLuaWhitespace(first_trimmed.substr(assign_pos + 1));
                    if (rhs == "..." &&
                        refii::lua::IsValidLuaIdentifier(local_name) &&
                        !refii::lua::IsLuaKeyword(local_name)) {
                        std::size_t second_content = first_content + 1;
                        while (second_content < lines.size() &&
                               refii::lua::TrimLuaWhitespace(
                                   refii::lua::StripLuaLineComment(lines[second_content]))
                                   .empty()) {
                            ++second_content;
                        }
                        if (second_content < lines.size()) {
                            const std::string_view second_trimmed = refii::lua::TrimLuaWhitespace(
                                refii::lua::StripLuaLineComment(lines[second_content]));
                            if (second_trimmed.starts_with("module(")) {
                                const std::size_t open = second_trimmed.find('(');
                                const std::size_t close = second_trimmed.rfind(')');
                                if (open != std::string_view::npos &&
                                    close != std::string_view::npos &&
                                    close > open + 1) {
                                    const std::string_view args =
                                        second_trimmed.substr(open + 1, close - open - 1);
                                    const std::size_t comma = args.find(',');
                                    const std::string_view first_arg =
                                        refii::lua::TrimLuaWhitespace(
                                            comma == std::string_view::npos
                                                ? args
                                                : args.substr(0, comma));
                                    if (first_arg == local_name) {
                                        const std::size_t line_indent_end =
                                            lines[second_content].find_first_not_of(" \t");
                                        const std::string line_indent =
                                            line_indent_end == std::string::npos
                                                ? std::string()
                                                : lines[second_content].substr(0, line_indent_end);
                                        const std::string suffix =
                                            comma == std::string::npos
                                                ? std::string(")")
                                                : std::string(args.substr(comma)) + ")";
                                        lines[second_content] = line_indent + "module(..."
                                                              + suffix;
                                        lines.erase(lines.begin() +
                                                    static_cast<std::ptrdiff_t>(first_content));
                                        normalized_module_vararg_seed = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    *source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "synthetic_root_wrapper_stripped");
        if (normalized_module_vararg_seed) {
            *note = JoinNotes(*note, "synthetic_root_wrapper_module_vararg_seed_normalized");
        }
    }
    return true;
}

refii::lua::internal::NativeCandidateFinalizeResult ClassifyNativeCandidateSource(
    const std::filesystem::path& relative_input,
    std::string_view candidate_source,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    const refii::lua::LuaWorkspaceDecompileOptions* options,
    std::string* note,
    std::string_view note_prefix) {
    refii::lua::internal::NativeCandidateFinalizeResult result;
    result.exact_parity_required = ShouldRequireExactParityForInputPolicy(
        relative_input, input_path, input_bytes, options);

    const std::string normalized_relative =
        NormalizePathString(relative_input.generic_string());
    const std::string validation_error =
        ValidateLuaSource(candidate_source, normalized_relative);
    if (!validation_error.empty()) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_invalid=" +
                                  SummarizeSyntaxError(validation_error));
        }
        return result;
    }

    if (!result.exact_parity_required) {
        result.kind = refii::lua::internal::NativeCandidateFinalizeKind::kExact;
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_validated");
        }
        return result;
    }

    refii::lua::audit::LuaBytecodeParityReference parity_reference;
    if (!refii::lua::audit::BuildLuaBytecodeParityReference(input_bytes, &parity_reference)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_parity_reference_unavailable");
        }
        return result;
    }

    refii::lua::audit::LuaBytecodeParityResult parity_result;
    if (TryEvaluateExactParityCandidate(parity_reference,
                                        candidate_source,
                                        normalized_relative,
                                        &parity_result)) {
        result.kind = refii::lua::internal::NativeCandidateFinalizeKind::kExact;
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_validated_exact_parity");
        }
        return result;
    }

    result.kind = refii::lua::internal::NativeCandidateFinalizeKind::kNonexact;
    if (note != nullptr && !note_prefix.empty()) {
        AppendParityClassificationNote(note, note_prefix, parity_result);
        *note = JoinNotes(*note, std::string(note_prefix) + "_validated_nonexact");
    }
    return result;
}

bool IsParityResultImproved(const refii::lua::audit::LuaBytecodeParityResult& candidate,
                            const refii::lua::audit::LuaBytecodeParityResult& baseline) {
    if (candidate.exact_bytecode != baseline.exact_bytecode) {
        return candidate.exact_bytecode;
    }

    const bool candidate_has_mismatch = candidate.first_mismatch.available;
    const bool baseline_has_mismatch = baseline.first_mismatch.available;
    if (candidate_has_mismatch != baseline_has_mismatch) {
        return !candidate_has_mismatch;
    }
    if (!candidate_has_mismatch) {
        return false;
    }

    if (candidate.first_mismatch.function_index != baseline.first_mismatch.function_index) {
        return candidate.first_mismatch.function_index > baseline.first_mismatch.function_index;
    }

    const auto candidate_delta_sum =
        std::llabs(candidate.first_mismatch.instruction_delta) +
        std::llabs(candidate.first_mismatch.constant_delta) +
        std::llabs(candidate.first_mismatch.local_delta);
    const auto baseline_delta_sum =
        std::llabs(baseline.first_mismatch.instruction_delta) +
        std::llabs(baseline.first_mismatch.constant_delta) +
        std::llabs(baseline.first_mismatch.local_delta);

    if (candidate_delta_sum != baseline_delta_sum) {
        return candidate_delta_sum < baseline_delta_sum;
    }

    const auto extract_instruction_diff_pc = [](const std::string& note,
                                                std::size_t* pc_out) {
        if (pc_out == nullptr) {
            return false;
        }
        constexpr std::string_view kPrefix = "instruction_diff pc=";
        const std::size_t pos = note.find(kPrefix);
        if (pos == std::string::npos) {
            return false;
        }
        std::size_t cursor = pos + kPrefix.size();
        std::size_t value = 0;
        bool saw_digit = false;
        while (cursor < note.size() &&
               std::isdigit(static_cast<unsigned char>(note[cursor]))) {
            saw_digit = true;
            value = value * 10 + static_cast<std::size_t>(note[cursor] - '0');
            ++cursor;
        }
        if (!saw_digit) {
            return false;
        }
        *pc_out = value;
        return true;
    };
    std::size_t candidate_pc = 0;
    std::size_t baseline_pc = 0;
    if (extract_instruction_diff_pc(candidate.first_mismatch.note, &candidate_pc) &&
        extract_instruction_diff_pc(baseline.first_mismatch.note, &baseline_pc) &&
        candidate_pc != baseline_pc) {
        return candidate_pc > baseline_pc;
    }

    return candidate.first_mismatch.note.size() < baseline.first_mismatch.note.size();
}

std::size_t CountSuspiciousSplitLocalCallResultUseBeforeAssignment(
    std::string_view source) {
    const std::vector<std::string> lines = refii::lua::SplitRenderedLines(source);
    std::size_t suspicious_count = 0;
    for (std::size_t line_index = 1; line_index < lines.size(); ++line_index) {
        const std::string_view previous_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(lines[line_index - 1]));
        if (!previous_trimmed.starts_with("local ") ||
            previous_trimmed.find('=') != std::string_view::npos) {
            continue;
        }

        const std::string second_result_name =
            std::string(refii::lua::TrimLuaWhitespace(previous_trimmed.substr(6)));
        if (second_result_name.empty() || second_result_name.find(',') != std::string::npos ||
            !refii::lua::IsValidLuaIdentifier(second_result_name) ||
            refii::lua::IsLuaKeyword(second_result_name)) {
            continue;
        }

        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!refii::lua::ParseRenderedAssignmentLine(lines[line_index],
                                                     &is_local,
                                                     &lhs_names,
                                                     &rhs_expression) ||
            !is_local || lhs_names.size() != 1 ||
            !refii::lua::IsValidLuaIdentifier(lhs_names.front()) ||
            refii::lua::IsLuaKeyword(lhs_names.front()) ||
            lhs_names.front() == second_result_name) {
            continue;
        }

        const std::string_view trimmed_rhs = refii::lua::TrimLuaWhitespace(rhs_expression);
        if (trimmed_rhs.empty() || trimmed_rhs.starts_with("function") ||
            trimmed_rhs.find('(') == std::string_view::npos ||
            !trimmed_rhs.ends_with(')')) {
            continue;
        }

        const std::size_t scan_limit = std::min(lines.size(), line_index + 8);
        for (std::size_t probe = line_index + 1; probe < scan_limit; ++probe) {
            bool probe_is_local = false;
            std::vector<std::string> probe_lhs_names;
            std::string probe_rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(lines[probe],
                                                       &probe_is_local,
                                                       &probe_lhs_names,
                                                       &probe_rhs_expression) &&
                std::find(probe_lhs_names.begin(),
                          probe_lhs_names.end(),
                          second_result_name) != probe_lhs_names.end()) {
                break;
            }

            if (refii::lua::CountIdentifierUses(
                    refii::lua::StripLuaLineComment(lines[probe]), second_result_name) != 0) {
                ++suspicious_count;
                break;
            }
        }
    }

    return suspicious_count;
}

bool IsValidatedRepairNameDisabledByEnvironment(std::string_view repair_name) {
    const char* env = std::getenv("REFII_LUA_DISABLE_VALIDATED_REPAIRS");
    if (env == nullptr || *env == '\0' || repair_name.empty()) {
        return false;
    }

    std::string_view value(env);
    std::size_t begin = 0;
    while (begin <= value.size()) {
        const std::size_t end = value.find(',', begin);
        const std::string_view token =
            TrimWhitespace(end == std::string_view::npos
                               ? value.substr(begin)
                               : value.substr(begin, end - begin));
        if (!token.empty() && token == repair_name) {
            return true;
        }
        if (end == std::string_view::npos) {
            break;
        }
        begin = end + 1;
    }
    return false;
}

bool CanPreferReadabilityGainOverParity(
    const refii::lua::audit::LuaBytecodeParityResult& candidate_parity,
    const refii::lua::audit::LuaBytecodeParityResult& baseline_parity) {
    if (candidate_parity.exact_bytecode || baseline_parity.exact_bytecode) {
        return candidate_parity.exact_bytecode && !baseline_parity.exact_bytecode;
    }
    if (!candidate_parity.first_mismatch.available ||
        !baseline_parity.first_mismatch.available) {
        return false;
    }
    if (candidate_parity.first_mismatch.function_index <
        baseline_parity.first_mismatch.function_index) {
        return false;
    }

    const long long candidate_delta_sum =
        std::llabs(candidate_parity.first_mismatch.instruction_delta) +
        std::llabs(candidate_parity.first_mismatch.constant_delta) +
        std::llabs(candidate_parity.first_mismatch.local_delta);
    const long long baseline_delta_sum =
        std::llabs(baseline_parity.first_mismatch.instruction_delta) +
        std::llabs(baseline_parity.first_mismatch.constant_delta) +
        std::llabs(baseline_parity.first_mismatch.local_delta);
    return candidate_delta_sum <= baseline_delta_sum + 16;
}

bool IsReadabilityResultImproved(std::string_view candidate_source,
                                 std::string_view baseline_source) {
    const LuaReadabilitySummary candidate_readability =
        SummarizeLuaReadability(candidate_source);
    const LuaReadabilitySummary baseline_readability =
        SummarizeLuaReadability(baseline_source);

    const bool candidate_registerized = LooksLikeRegisterizedLua(candidate_readability);
    const bool baseline_registerized = LooksLikeRegisterizedLua(baseline_readability);
    if (candidate_registerized != baseline_registerized) {
        return !candidate_registerized;
    }

    if (candidate_readability.temporary_self_reference_assignment_count !=
        baseline_readability.temporary_self_reference_assignment_count) {
        return candidate_readability.temporary_self_reference_assignment_count <
               baseline_readability.temporary_self_reference_assignment_count;
    }

    if (candidate_readability.temporary_leading_line_count !=
        baseline_readability.temporary_leading_line_count) {
        return candidate_readability.temporary_leading_line_count <
               baseline_readability.temporary_leading_line_count;
    }

    if (candidate_readability.temporary_assignment_count !=
        baseline_readability.temporary_assignment_count) {
        return candidate_readability.temporary_assignment_count <
               baseline_readability.temporary_assignment_count;
    }

    const std::size_t candidate_risk = ScoreLuaReadabilityRisk(candidate_readability);
    const std::size_t baseline_risk = ScoreLuaReadabilityRisk(baseline_readability);
    if (candidate_risk != baseline_risk) {
        return candidate_risk < baseline_risk;
    }

    return candidate_source.size() < baseline_source.size();
}

bool ShouldPreferValidatedNonexactCandidate(
    std::string_view candidate_source,
    const refii::lua::audit::LuaBytecodeParityResult& candidate_parity,
    std::string_view baseline_source,
    const refii::lua::audit::LuaBytecodeParityResult& baseline_parity,
    std::string* preference_reason) {
    if (preference_reason != nullptr) {
        preference_reason->clear();
    }
    const bool tiny_baseline_instruction_or_local_mismatch =
        baseline_parity.first_mismatch.available &&
        std::llabs(baseline_parity.first_mismatch.instruction_delta) <= 1 &&
        std::llabs(baseline_parity.first_mismatch.constant_delta) == 0 &&
        std::llabs(baseline_parity.first_mismatch.local_delta) <= 2;
    if (tiny_baseline_instruction_or_local_mismatch && !candidate_parity.exact_bytecode) {
        const bool tiny_candidate_instruction_or_local_mismatch =
            candidate_parity.first_mismatch.available &&
            std::llabs(candidate_parity.first_mismatch.instruction_delta) <= 1 &&
            std::llabs(candidate_parity.first_mismatch.constant_delta) == 0 &&
            std::llabs(candidate_parity.first_mismatch.local_delta) <= 2;
        if (!tiny_candidate_instruction_or_local_mismatch) {
            return false;
        }
    }

    const std::size_t candidate_split_call_result_bug_count =
        CountSuspiciousSplitLocalCallResultUseBeforeAssignment(candidate_source);
    const std::size_t baseline_split_call_result_bug_count =
        CountSuspiciousSplitLocalCallResultUseBeforeAssignment(baseline_source);
    const bool enable_splitlocalcallresults_preference =
        !IsValidatedRepairNameDisabledByEnvironment("splitlocalcallresults");
    if (enable_splitlocalcallresults_preference &&
        candidate_split_call_result_bug_count != baseline_split_call_result_bug_count &&
        CanPreferReadabilityGainOverParity(candidate_parity, baseline_parity)) {
        if (preference_reason != nullptr) {
            *preference_reason = "splitlocalcallresults";
        }
        return candidate_split_call_result_bug_count < baseline_split_call_result_bug_count;
    }

    if (IsParityResultImproved(candidate_parity, baseline_parity)) {
        return true;
    }
    if (IsParityResultImproved(baseline_parity, candidate_parity)) {
        return false;
    }
    if (tiny_baseline_instruction_or_local_mismatch) {
        // For the high-volume ±1/0/0 conversion lane, avoid replacing a parity-equivalent
        // candidate only for readability. Keep the closest parity shape stable so later
        // bytecode-backed exactification has a deterministic baseline.
        return false;
    }
    return IsReadabilityResultImproved(candidate_source, baseline_source);
}

long long SumParityMismatchDelta(
    const refii::lua::audit::LuaBytecodeParityResult& parity_result) {
    if (!parity_result.first_mismatch.available) {
        return 0;
    }

    return std::llabs(parity_result.first_mismatch.instruction_delta) +
           std::llabs(parity_result.first_mismatch.constant_delta) +
           std::llabs(parity_result.first_mismatch.local_delta);
}

bool ShouldSkipValidatedParityCleanupCandidate(
    const refii::lua::audit::LuaBytecodeParityReference& parity_reference,
    const refii::lua::audit::LuaBytecodeParityResult& parity_result,
    std::string_view source,
    std::string* reason_out) {
    if (reason_out != nullptr) {
        reason_out->clear();
    }
    if (parity_result.exact_bytecode || !parity_result.first_mismatch.available) {
        return false;
    }

    const long long delta_sum = SumParityMismatchDelta(parity_result);
    const std::size_t reference_instruction_count =
        parity_reference.full_stats.instruction_count;
    const std::size_t candidate_instruction_count =
        parity_result.compiled_candidate.full_stats.instruction_count;
    const std::size_t max_instruction_count =
        std::max(reference_instruction_count, candidate_instruction_count);
    const std::size_t source_size = source.size();

    if (max_instruction_count >= 12000 && delta_sum >= 64) {
        if (reason_out != nullptr) {
            *reason_out = "large_body_delta";
        }
        return true;
    }
    if ((max_instruction_count >= 6000 || source_size >= 128 * 1024) && delta_sum >= 128) {
        if (reason_out != nullptr) {
            *reason_out = "wide_delta";
        }
        return true;
    }
    if ((max_instruction_count >= 5000 || source_size >= 96 * 1024) &&
        parity_result.first_mismatch.function_index >= 8 && delta_sum >= 32) {
        if (reason_out != nullptr) {
            *reason_out = "deep_large_delta";
        }
        return true;
    }

    return false;
}

#include "decompiler/rendered/pipelines/validated_cleanup_pipeline.inc"

bool TryApplyValidatedParityImprovingCleanupCandidate(
    std::string* source,
    const std::vector<char>& input_bytes,
    const refii::lua::audit::LuaBytecodeParityReference& parity_reference,
    const std::filesystem::path& output_path,
    std::string* note,
    std::string_view note_prefix) {
    if (source == nullptr) {
        return false;
    }

    const auto evaluate_candidate_parity =
        [&](const std::string& candidate_source,
            refii::lua::audit::LuaBytecodeParityResult* parity_result) {
        if (parity_result == nullptr) {
            return false;
        }
        return refii::lua::audit::EvaluateLuaSourceBytecodeParity(
            parity_reference, candidate_source, output_path.generic_string(), parity_result);
    };

    refii::lua::audit::LuaBytecodeParityResult current_parity;
    if (!evaluate_candidate_parity(*source, &current_parity)) {
        return false;
    }
    std::string skip_reason;
    if (ShouldSkipValidatedParityCleanupCandidate(
            parity_reference, current_parity, *source, &skip_reason)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_validated_parity_cleanup_skipped=" + skip_reason);
        }
        return false;
    }

    const auto apply_cleanup = [&](std::string source_text,
                                   refii::lua::audit::LuaBytecodeParityResult* parity_state,
                                   bool* changed_any) {
        if (parity_state == nullptr) {
            return source_text;
        }

        const ValidatedRenderedCleanupPipelineOptions cleanup_options{
            .include_dead_temporary_alias_cleanup = true,
            .include_namespace_alias_cleanup = true,
            .include_ordered_compare_self_cleanup_tail = true,
            .include_nested_duplicate_return_guard_cleanup = true,
            .include_repeated_multi_return_predicate_cleanup = true,
        };
        const auto keep_candidate = [&](const std::string& candidate_source) {
            refii::lua::audit::LuaBytecodeParityResult candidate_parity;
            if (!evaluate_candidate_parity(candidate_source, &candidate_parity)) {
                return false;
            }
            if (!candidate_parity.exact_bytecode &&
                !IsParityResultImproved(candidate_parity, *parity_state)) {
                return false;
            }
            *parity_state = std::move(candidate_parity);
            return true;
        };
        return ApplyValidatedRenderedCleanupPipeline(std::move(source_text),
                                                     cleanup_options,
                                                     keep_candidate,
                                                     changed_any);
    };

    const std::string original_source = *source;
    std::string candidate_source = apply_cleanup(original_source, &current_parity, nullptr);
    std::string restored_source = candidate_source;
    if (refii::lua::decompiler::TryApplyBytecodeBackedMainChunkRestorations(
            input_bytes,
            output_path.filename().generic_string(),
            &restored_source,
            note,
            note_prefix.empty() ? "validated_parity_cleanup"
                                : std::string(note_prefix) + "_validated_parity_cleanup")) {
        refii::lua::audit::LuaBytecodeParityResult restored_parity;
        if (evaluate_candidate_parity(restored_source, &restored_parity) &&
            (restored_parity.exact_bytecode ||
             IsParityResultImproved(restored_parity, current_parity))) {
            current_parity = std::move(restored_parity);
            if (restored_source != candidate_source) {
                candidate_source =
                    apply_cleanup(std::move(restored_source), &current_parity, nullptr);
            }
        }
    }

    if (candidate_source == original_source) {
        return false;
    }

    *source = std::move(candidate_source);
    if (note != nullptr && !note_prefix.empty()) {
        *note = JoinNotes(*note,
                          std::string(note_prefix) +
                              (current_parity.exact_bytecode
                                   ? "_validated_exact_cleanup"
                                   : "_validated_parity_improved_cleanup"));
    }
    return true;
}

bool FinalizeValidatedLuaOutput(std::string_view source,
                                const std::vector<char>& input_bytes,
                                const refii::lua::LuaWorkspaceDecompileOptions* options,
                                const std::filesystem::path* input_path,
                                const std::filesystem::path& output_path,
                                const std::filesystem::path& failed_output_path,
                                std::string* note,
                                const refii::lua::audit::LuaBytecodeParityReference* parity_reference =
                                    nullptr,
                                bool require_exact_parity = false) {
    const auto deadline_exceeded = [&]() {
        return ShouldAbortCurrentDecompileRepair(note, "validated_output");
    };
    std::string final_source(source);
    const std::string pre_finalize_parity_floor_source = final_source;
    const std::string quality_scope_path = NormalizePathString(
        input_path != nullptr ? input_path->generic_string()
                              : output_path.generic_string());
    const refii::lua::LuaScriptPolicy finalize_output_policy =
        refii::lua::BuildLuaScriptPolicy(quality_scope_path);
    const bool runtime_light_cleanup_path =
        !require_exact_parity &&
        ((IsRuntimeLightModePath(finalize_output_policy, input_bytes.size()) ||
          ShouldClampRuntimeHeavyRepairFamilies(finalize_output_policy, input_bytes.size())) &&
         (input_bytes.size() >= 160 * 1024 || final_source.size() >= 160 * 1024));
    refii::lua::internal::LuaMutableTextContext final_text_context(final_source);
    const auto validate_final_source = [&](std::string_view validation_label) {
        final_text_context.SetSourceView(final_source);
        return final_text_context.ValidateCached(validation_label);
    };
    const auto validate_candidate_source = [&](std::string_view candidate_source,
                                               std::string_view validation_label) {
        final_text_context.SetSourceView(candidate_source);
        return final_text_context.ValidateCached(validation_label);
    };
    const auto accept_nonworse_nonexact_parity_candidate =
        [&](const std::string& candidate_source,
            std::string_view rejected_note_suffix) {
            if (require_exact_parity || parity_reference == nullptr) {
                return true;
            }

            refii::lua::audit::LuaBytecodeParityResult baseline_parity;
            if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                    *parity_reference,
                    final_source,
                    output_path.generic_string(),
                    &baseline_parity)) {
                return true;
            }

            refii::lua::audit::LuaBytecodeParityResult candidate_parity;
            if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                    *parity_reference,
                    candidate_source,
                    output_path.generic_string(),
                    &candidate_parity)) {
                return true;
            }

            if (IsParityResultImproved(baseline_parity, candidate_parity)) {
                if (note != nullptr && !rejected_note_suffix.empty()) {
                    *note = JoinNotes(*note,
                                      std::string(rejected_note_suffix) +
                                          "_parity_regression_rejected");
                }
                return false;
            }

            return true;
        };
    const auto try_write_failed_output = [&]() {
        if (failed_output_path.empty()) {
            return;
        }
        std::filesystem::create_directories(failed_output_path.parent_path());
        if (WriteNormalizedLuaTextFile(failed_output_path, final_source, note)) {
            if (note != nullptr) {
                *note = JoinNotes(*note, "failed_output_saved");
            }
        } else if (note != nullptr) {
            *note = JoinNotes(*note, "failed_output_save_failed");
        }
    };
    TryStripSyntheticRootWrapperSource(&final_source, note);
    if (final_source.find("do return end") != std::string::npos) {
        std::string repaired_source = final_source;
        if (TryStripStandaloneDoReturnEndArtifacts(repaired_source, note) &&
            validate_candidate_source(repaired_source, output_path.generic_string()).empty()) {
            refii::lua::audit::LuaBytecodeParityResult repaired_parity;
            const bool repaired_exact =
                !require_exact_parity ||
                (parity_reference != nullptr &&
                 TryEvaluateExactParityCandidate(*parity_reference,
                                                repaired_source,
                                                output_path.generic_string(),
                                                &repaired_parity));
            if (!repaired_exact) {
                repaired_source = final_source;
            } else {
            final_source = std::move(repaired_source);
            }
        }
    }

    const std::string pre_validated_cleanup_source = final_source;
    const std::size_t pre_validated_cleanup_undefined_temp_uses =
        CountLikelyUndefinedTemporaryUses(final_source);
    if (!runtime_light_cleanup_path) {
        const std::string pre_cleanup_source = final_source;
        const auto compute_validated_cleanup_source = [&]() {
            const auto apply_validated_cleanup =
                [&](std::string source_text, bool* changed_any) -> std::string {
                const ValidatedRenderedCleanupPipelineOptions cleanup_options{
                    .include_dead_temporary_alias_cleanup = false,
                    .include_namespace_alias_cleanup = false,
                    .include_ordered_compare_self_cleanup_tail = false,
                };
                const auto keep_candidate = [&](const std::string& candidate_source) {
                    if (!validate_candidate_source(candidate_source,
                                                  output_path.generic_string())
                             .empty()) {
                        return false;
                    }
                    if (require_exact_parity && parity_reference != nullptr) {
                        refii::lua::audit::LuaBytecodeParityResult parity_result;
                        if (!TryEvaluateExactParityCandidate(*parity_reference,
                                                             candidate_source,
                                                             output_path.generic_string(),
                                                             &parity_result)) {
                            return false;
                        }
                    }
                    return true;
                };
                return ApplyValidatedRenderedCleanupPipeline(std::move(source_text),
                                                             cleanup_options,
                                                             keep_candidate,
                                                             changed_any);
            };

            bool cleanup_changed = false;
            std::string candidate_source =
                apply_validated_cleanup(final_source, &cleanup_changed);
            std::string restored_source = candidate_source;
            if (refii::lua::decompiler::TryApplyBytecodeBackedMainChunkRestorations(
                    input_bytes,
                    output_path.filename().generic_string(),
                    &restored_source,
                    note,
                    "validated_output_cleanup")) {
                if (validate_candidate_source(restored_source, output_path.generic_string())
                        .empty()) {
                    bool keep_restored_source = true;
                    if (require_exact_parity && parity_reference != nullptr) {
                        refii::lua::audit::LuaBytecodeParityResult parity_result;
                        keep_restored_source =
                            TryEvaluateExactParityCandidate(*parity_reference,
                                                            restored_source,
                                                            output_path.generic_string(),
                                                            &parity_result);
                    }
                    if (keep_restored_source) {
                        if (restored_source != candidate_source) {
                            candidate_source = std::move(restored_source);
                            candidate_source =
                                apply_validated_cleanup(candidate_source, nullptr);
                        }
                    }
                }
            }
            return candidate_source;
        };

        if (ShouldCacheValidatedCleanupSource(pre_cleanup_source)) {
            MaybeTrimTransientCleanupCache();
            const std::string cleanup_cache_key = BuildCleanupCacheKey(
                refii::lua::internal::RenderedLuaCleanupProfile::kValidatedOutput,
                pre_cleanup_source);
            CleanupCacheEntry& cleanup_cache_entry =
                g_lua_decompiler_session_cache.cleanup_results[cleanup_cache_key];
            if (!cleanup_cache_entry.attempted) {
                cleanup_cache_entry.attempted = true;
                cleanup_cache_entry.cleaned_source = compute_validated_cleanup_source();
            }
            final_source = cleanup_cache_entry.cleaned_source;
        } else {
            final_source = compute_validated_cleanup_source();
        }

        const bool skip_validated_cleanup_reference_repair_runtime_guard =
            pre_cleanup_source.size() >= 96 * 1024 || final_source.size() >= 96 * 1024;
        if (!require_exact_parity &&
            !skip_validated_cleanup_reference_repair_runtime_guard &&
            final_source != pre_cleanup_source) {
            RunBoundedReferenceRepairLoop(
                GetCurrentReferenceRepairPassBudget(),
                true,
                &final_source,
                nullptr,
                note,
                "validated_output_cleanup_no_progress",
                [&]() { return deadline_exceeded(); },
                [&]() {
                    return TryRepairCandidateMismatchedFunctionsFromReference(
                        input_bytes,
                        pre_cleanup_source,
                        &final_source,
                        note,
                        "validated_output_cleanup");
                },
                [&]() {
                    return validate_final_source("validated_output_cleanup");
                });
        } else if (!require_exact_parity &&
                   skip_validated_cleanup_reference_repair_runtime_guard &&
                   note != nullptr) {
            *note = JoinNotes(*note,
                              "validated_output_cleanup_reference_skipped_runtime_guard");
        }
    } else if (note != nullptr) {
        *note = JoinNotes(*note, "validated_output_cleanup_runtime_light_skipped");
    }

    if (!require_exact_parity && !runtime_light_cleanup_path) {
        const int post_cleanup_parity_pass_cap = GetCurrentRepairAttemptCap(2, 1);
        RunBoundedNormalizationPassLoop(post_cleanup_parity_pass_cap, [&](int /*pass*/) {
            if (deadline_exceeded()) {
                return false;
            }
            if (!TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                        final_source,
                                                        note,
                                                        "validated_output_post_cleanup",
                                                        options,
                                                        input_path)) {
                return false;
            }
            return true;
        });
    } else if (!require_exact_parity && runtime_light_cleanup_path && note != nullptr) {
        *note = JoinNotes(*note, "validated_output_post_cleanup_parity_runtime_light_skipped");
    }

    if (!require_exact_parity) {
        const std::size_t final_undefined_temporary_uses =
            CountLikelyUndefinedTemporaryUses(final_source);
        if (final_undefined_temporary_uses > pre_validated_cleanup_undefined_temp_uses) {
            const std::string fallback_validation_error =
                validate_candidate_source(pre_validated_cleanup_source,
                                          output_path.generic_string());
            if (fallback_validation_error.empty()) {
                final_source = pre_validated_cleanup_source;
                if (note != nullptr) {
                    *note = JoinNotes(
                        *note,
                        "validated_output_reverted_undefined_temporary_use_regression=" +
                            std::to_string(final_undefined_temporary_uses) + ">" +
                            std::to_string(pre_validated_cleanup_undefined_temp_uses));
                }
            }
        }
    }

    if (!require_exact_parity && !runtime_light_cleanup_path) {
        refii::lua::internal::LuaMutableTextContext safety_context(final_source);
        std::vector<std::string>* safety_lines = safety_context.EnsureMutableLines();
        bool safety_changed = false;
        safety_changed |=
            refii::lua::SimplifyRenderedLuaRedundantIpairsTableReseeds(safety_lines);
        safety_changed |=
            SimplifyRenderedLuaRedundantTableReseedsBeforeFieldWrites(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaSameTargetSingletonLiteralOverwrites(safety_lines);
        safety_changed |= ApplyRenderedLuaNearExactSingletonSeedCluster(
            safety_lines, false, false);
        safety_changed |=
            NormalizeRenderedLuaSparseSingletonTableReplacementChains(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaNearExactTopLevelSingletonFieldSeeds(safety_lines);
        safety_changed |= ApplyRenderedLuaSeedNormalizations(
            safety_lines, RenderedLuaSeedNormalizationLevel::kExtended);
        // Keep same-target singleton overwrite repair after broader seed
        // normalizations so they cannot re-expand proven field-patch forms.
        safety_changed |= ApplyRenderedLuaNearExactSingletonSeedCluster(
            safety_lines, false, false);
        safety_changed |=
            NormalizeRenderedLuaSameTargetSingletonLiteralOverwrites(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaEmptyTableThenSingletonIndexAssignmentToLiteral(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaDuplicateGroupMethodCallChains(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaRedundantExistingTableReseedsBeforeFieldWrite(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaImmediateLocalTableLiteralCallPlaceholder(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaSeededGuardedCallArgumentScaffolds(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaAdjacentCopiedLocalCallSeeds(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaRedundantRepeatedCallReassignments(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaFailureGuardBooleanReturns(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaCallPredicateNotToFalseCompare(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaWhileTrueNestedNotBreakToOrGuard(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaEmptyNotGuardBeforeReturnToOrReturn(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaTimingFrameDeltaGuardScaffolds(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaNavigationAndMoveGuardNesting(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaMoveToPositionFailureElseBlocks(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaPairsCallLocalTableSeeds(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaInlineTableInsertRecordLocals(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaInverseDistanceLocalTiming(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaRepeatedSubtractThresholdComparisons(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaInlineLoopActionSetCurrentActionLocals(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaIdleActionTimerScaffolds(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaSingletonLiteralThenIndexPatchChain(safety_lines);
        safety_changed |=
            NormalizeRenderedLuaBareIndexPatchChainToLiteralAssignment(safety_lines);
        if (safety_changed) {
            safety_context.MarkLinesMutated();
            const std::string candidate_safety_source = std::string(safety_context.EnsureSource());
            if (validate_candidate_source(candidate_safety_source,
                                          output_path.generic_string())
                    .empty() &&
                accept_nonworse_nonexact_parity_candidate(
                    candidate_safety_source, "validated_output_runtime_safety_cleanup")) {
                final_source = std::move(candidate_safety_source);
                if (note != nullptr) {
                    *note = JoinNotes(*note, "validated_output_runtime_safety_cleanup");
                }
            }
        }
    } else if (!require_exact_parity && runtime_light_cleanup_path && note != nullptr) {
        *note = JoinNotes(*note, "validated_output_runtime_safety_cleanup_skipped_runtime_light");
    }

    if (!runtime_light_cleanup_path) {
        refii::lua::internal::LuaMutableTextContext hazard_context(final_source);
        std::vector<std::string>* hazard_lines = hazard_context.EnsureMutableLines();
        bool hazard_changed = false;
        hazard_changed |= ApplyRenderedLuaSeedNormalizations(
            hazard_lines, RenderedLuaSeedNormalizationLevel::kHazard);
        if (hazard_changed) {
            hazard_context.MarkLinesMutated();
            const std::string candidate_hazard_source = std::string(hazard_context.EnsureSource());
            if (validate_candidate_source(candidate_hazard_source,
                                          output_path.generic_string())
                    .empty() &&
                accept_nonworse_nonexact_parity_candidate(
                    candidate_hazard_source, "validated_output_runtime_hazard_cleanup")) {
                final_source = std::move(candidate_hazard_source);
                if (note != nullptr) {
                    *note = JoinNotes(*note, "validated_output_runtime_hazard_literal_nil_cleanup");
                }
            }
        }
    } else if (note != nullptr) {
        *note = JoinNotes(*note,
                          "validated_output_runtime_hazard_cleanup_skipped_runtime_light");
    }

    if (!require_exact_parity && !runtime_light_cleanup_path) {
        refii::lua::internal::LuaMutableTextContext post_hazard_context(final_source);
        std::vector<std::string>* post_hazard_lines =
            post_hazard_context.EnsureMutableLines();
        bool post_hazard_changed = false;
        post_hazard_changed |=
            NormalizeRenderedLuaRedundantExistingTableReseedsBeforeFieldWrite(
                post_hazard_lines);
        if (post_hazard_changed) {
            post_hazard_context.MarkLinesMutated();
            const std::string candidate_post_hazard_source =
                std::string(post_hazard_context.EnsureSource());
            if (validate_candidate_source(candidate_post_hazard_source,
                                          output_path.generic_string())
                    .empty() &&
                accept_nonworse_nonexact_parity_candidate(
                    candidate_post_hazard_source,
                    "validated_output_post_hazard_reseed_cleanup")) {
                final_source = std::move(candidate_post_hazard_source);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "validated_output_post_hazard_reseed_cleanup");
                }
            }
        }
    }

    if (!require_exact_parity && !runtime_light_cleanup_path &&
        input_bytes.size() <= 128 * 1024 &&
        final_source.size() <= 192 * 1024) {
        const std::string pre_post_safety_parity_source = final_source;
        if (TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                   final_source,
                                                   note,
                                                   "validated_output_post_safety",
                                                   options,
                                                   input_path)) {
            const std::string repaired_validation_error =
                validate_candidate_source(final_source, output_path.generic_string());
            if (!repaired_validation_error.empty()) {
                final_source = pre_post_safety_parity_source;
            } else if (final_source == pre_post_safety_parity_source) {
                // Avoid reporting a synthetic step when the bounded parity pass
                // produced no effective source delta.
                if (note != nullptr) {
                    *note = JoinNotes(*note, "validated_output_post_safety_no_progress");
                }
            }
        }
    }

    if (!require_exact_parity && !runtime_light_cleanup_path) {
        refii::lua::internal::LuaMutableTextContext final_shape_context(final_source);
        std::vector<std::string>* final_shape_lines =
            final_shape_context.EnsureMutableLines();
        bool final_shape_changed = false;
        final_shape_changed |=
            NormalizeRenderedLuaBareIndexPatchChainToLiteralAssignment(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaSeededGuardedCallArgumentScaffolds(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaAdjacentCopiedLocalCallSeeds(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaRedundantRepeatedCallReassignments(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaFailureGuardBooleanReturns(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaCallPredicateNotToFalseCompare(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaWhileTrueNestedNotBreakToOrGuard(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaEmptyNotGuardBeforeReturnToOrReturn(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaTimingFrameDeltaGuardScaffolds(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaNavigationAndMoveGuardNesting(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaMoveToPositionFailureElseBlocks(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaPairsCallLocalTableSeeds(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaInlineTableInsertRecordLocals(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaInverseDistanceLocalTiming(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaRepeatedSubtractThresholdComparisons(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaInlineLoopActionSetCurrentActionLocals(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaIdleActionTimerScaffolds(final_shape_lines);
        final_shape_changed |=
            NormalizeRenderedLuaSingletonLiteralThenIndexPatchChain(final_shape_lines);
        if (final_shape_changed) {
            final_shape_context.MarkLinesMutated();
            const std::string candidate_final_shape_source =
                std::string(final_shape_context.EnsureSource());
            if (validate_candidate_source(candidate_final_shape_source,
                                          output_path.generic_string())
                    .empty() &&
                accept_nonworse_nonexact_parity_candidate(
                    candidate_final_shape_source,
                    "validated_output_final_constructor_shape_cleanup")) {
                final_source = std::move(candidate_final_shape_source);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "validated_output_final_constructor_shape_cleanup");
                }
            }
        }
    }

    if (!require_exact_parity && !runtime_light_cleanup_path &&
        input_bytes.size() <= 128 * 1024 &&
        final_source.size() <= 192 * 1024) {
        const std::string pre_final_shape_parity_source = final_source;
        if (TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                   final_source,
                                                   note,
                                                   "validated_output_final_shape",
                                                   options,
                                                   input_path)) {
            const std::string repaired_validation_error =
                validate_candidate_source(final_source, output_path.generic_string());
            if (!repaired_validation_error.empty()) {
                final_source = pre_final_shape_parity_source;
            } else if (final_source == pre_final_shape_parity_source && note != nullptr) {
                *note = JoinNotes(*note, "validated_output_final_shape_no_progress");
            }
        }
    }

    if (!require_exact_parity && !runtime_light_cleanup_path &&
        parity_reference != nullptr &&
        input_bytes.size() <= 128 * 1024 &&
        final_source.size() <= 192 * 1024) {
        const std::string pre_final_shape_validated_source = final_source;
        if (TryApplyValidatedParityImprovingCleanupCandidate(
                &final_source,
                input_bytes,
                *parity_reference,
                output_path,
                note,
                "validated_output_final_shape")) {
            const std::string repaired_validation_error =
                validate_candidate_source(final_source, output_path.generic_string());
            if (!repaired_validation_error.empty()) {
                final_source = pre_final_shape_validated_source;
            }
        }
    }

    if (!require_exact_parity && !runtime_light_cleanup_path &&
        parity_reference != nullptr &&
        input_bytes.size() <= 128 * 1024 &&
        final_source.size() <= 192 * 1024) {
        const auto try_late_parity_shape =
            [&](bool (*transform)(std::vector<std::string>*),
                std::string_view note_suffix) {
            refii::lua::internal::LuaMutableTextContext shape_context(final_source);
            std::vector<std::string>* shape_lines = shape_context.EnsureMutableLines();
            if (!transform(shape_lines)) {
                return;
            }
            shape_context.MarkLinesMutated();
            const std::string candidate_source =
                std::string(shape_context.EnsureSource());
            if (candidate_source == final_source) {
                return;
            }
            if (validate_candidate_source(candidate_source, output_path.generic_string())
                    .empty()) {
                refii::lua::audit::LuaBytecodeParityResult baseline_parity;
                refii::lua::audit::LuaBytecodeParityResult candidate_parity;
                const auto full_stats_distance =
                    [&](const refii::lua::audit::LuaBytecodeParityResult& result) {
                    const refii::lua::Lua51ChunkStats& candidate_stats =
                        result.compiled_candidate.full_stats;
                    const refii::lua::Lua51ChunkStats& reference_stats =
                        parity_reference->full_stats;
                    return std::llabs(static_cast<long long>(
                                          candidate_stats.instruction_count) -
                                      static_cast<long long>(
                                          reference_stats.instruction_count)) +
                           std::llabs(static_cast<long long>(
                                          candidate_stats.constant_count) -
                                      static_cast<long long>(
                                          reference_stats.constant_count)) +
                           std::llabs(static_cast<long long>(
                                          candidate_stats.local_count) -
                                      static_cast<long long>(
                                          reference_stats.local_count));
                };
                if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        *parity_reference,
                        final_source,
                        output_path.generic_string(),
                        &baseline_parity) &&
                    refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        *parity_reference,
                        candidate_source,
                        output_path.generic_string(),
                        &candidate_parity) &&
                    (candidate_parity.exact_bytecode ||
                     IsParityResultImproved(candidate_parity, baseline_parity) ||
                     full_stats_distance(candidate_parity) <
                         full_stats_distance(baseline_parity))) {
                    final_source = std::move(candidate_source);
                    if (note != nullptr) {
                        *note = JoinNotes(
                            *note,
                            candidate_parity.exact_bytecode
                                ? std::string("validated_output_final_") +
                                      std::string(note_suffix) + "_exact"
                                : std::string("validated_output_final_") +
                                      std::string(note_suffix) + "_improved");
                    }
                }
            }
        };

        try_late_parity_shape(NormalizeRenderedLuaRepeatedMultiReturnPredicateForParity,
                              "multi_return_predicate");
        try_late_parity_shape(NormalizeRenderedLuaSequentialPredicateBodyToElseForParity,
                              "predicate_else_body");
        try_late_parity_shape(
            NormalizeRenderedLuaTalkerPartnerAnimationElseForParity,
            "talker_partner_animation_else");
        try_late_parity_shape(NormalizeRenderedLuaWhileTrueYieldGuardBreakForParity,
                              "yield_guard_break");
        try_late_parity_shape(
            NormalizeRenderedLuaFirstSimpleCallFalseCompareToNotForParity,
            "first_call_false_compare_to_not");
        try_late_parity_shape(
            NormalizeRenderedLuaFirstAndPredicateElseToNestedForParity,
            "first_and_predicate_else_nested");
        try_late_parity_shape(
            NormalizeRenderedLuaFirstImmediateVectorLocalCallArgumentForParity,
            "first_vector_local_call_argument");
        try_late_parity_shape(
            NormalizeRenderedLuaFirstMoveToPositionLoopVectorParityShape,
            "first_move_to_position_loop_vector_shape");
    }

    if (!require_exact_parity && !runtime_light_cleanup_path) {
        refii::lua::internal::LuaMutableTextContext final_idle_context(final_source);
        std::vector<std::string>* final_idle_lines =
            final_idle_context.EnsureMutableLines();
        bool final_idle_changed = false;
        final_idle_changed |=
            NormalizeRenderedLuaNavigationAndMoveGuardNesting(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaWhileTrueNestedNotBreakToOrGuard(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaEmptyNotGuardBeforeReturnToOrReturn(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaTimingFrameDeltaGuardScaffolds(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaMoveToPositionFailureElseBlocks(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaPairsCallLocalTableSeeds(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaInlineTableInsertRecordLocals(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaInverseDistanceLocalTiming(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaRepeatedSubtractThresholdComparisons(final_idle_lines);
        final_idle_changed |=
            NormalizeRenderedLuaInlineLoopActionSetCurrentActionLocals(final_idle_lines);
        final_idle_changed |= NormalizeRenderedLuaIdleActionTimerScaffolds(final_idle_lines);
        final_idle_changed |= NormalizeRenderedLuaSimpleCallFalseComparesToNot(final_idle_lines);
        if (final_idle_changed) {
            final_idle_context.MarkLinesMutated();
            const std::string candidate_final_idle_source =
                std::string(final_idle_context.EnsureSource());
            if (validate_candidate_source(candidate_final_idle_source,
                                          output_path.generic_string())
                    .empty() &&
                accept_nonworse_nonexact_parity_candidate(
                    candidate_final_idle_source,
                    "validated_output_final_idle_timer_cleanup")) {
                final_source = std::move(candidate_final_idle_source);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "validated_output_final_idle_timer_cleanup");
                }
            }
        }
    }

    if (!require_exact_parity && parity_reference != nullptr && note != nullptr) {
        const auto try_final_parity_normalization =
            [&](std::string_view marker,
                bool (*transform)(std::vector<std::string>*),
                std::string_view accepted_note) -> bool {
            if (note->find(marker) == std::string::npos) {
                return false;
            }
            refii::lua::internal::LuaMutableTextContext parity_context(final_source);
            std::vector<std::string>* parity_lines = parity_context.EnsureMutableLines();
            if (!transform(parity_lines)) {
                return false;
            }
            parity_context.MarkLinesMutated();
            const std::string candidate_parity_source =
                std::string(parity_context.EnsureSource());
            if (validate_candidate_source(candidate_parity_source,
                                          output_path.generic_string())
                    .empty()) {
                refii::lua::audit::LuaBytecodeParityResult baseline_parity;
                refii::lua::audit::LuaBytecodeParityResult candidate_parity;
                if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        *parity_reference,
                        final_source,
                        output_path.generic_string(),
                        &baseline_parity) &&
                    refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        *parity_reference,
                        candidate_parity_source,
                        output_path.generic_string(),
                        &candidate_parity) &&
                    (candidate_parity.exact_bytecode ||
                     IsParityResultImproved(candidate_parity, baseline_parity))) {
                    final_source = std::move(candidate_parity_source);
                    *note = JoinNotes(*note, std::string(accepted_note));
                    return true;
                }
            }
            return false;
        };

        bool final_parity_changed = false;
        final_parity_changed |= try_final_parity_normalization(
            "_parity_improving_normalized_leading_call_comparisons",
            refii::lua::TrySwapRenderedLeadingCallComparisonsForExactParity,
            "validated_output_final_parity_leading_call_compare");
        final_parity_changed |= try_final_parity_normalization(
            "_parity_improving_normalized_negated_boolean_returns",
            refii::lua::ExpandRenderedLuaNegatedBooleanReturnsForParity,
            "validated_output_final_parity_negated_boolean_returns");
        final_parity_changed |= try_final_parity_normalization(
            "_parity_improving_normalized_returned_calls_wrapped",
            refii::lua::WrapRenderedLuaReturnedCallsForExactParity,
            "validated_output_final_parity_returned_calls_wrapped");
        final_parity_changed |= try_final_parity_normalization(
            "_parity_improving_normalized_returned_calls_unwrapped",
            refii::lua::UnwrapRenderedLuaReturnedCallsForExactParity,
            "validated_output_final_parity_returned_calls_unwrapped");
        final_parity_changed |= try_final_parity_normalization(
            "_parity_improving_normalized_loop_break_else_post_if",
            refii::lua::NormalizeRenderedLuaLoopBreakElseToPostIfForParity,
            "validated_output_final_parity_loop_break_else_post_if");
        (void)final_parity_changed;
    }

    if (!require_exact_parity && parity_reference != nullptr &&
        final_source != pre_finalize_parity_floor_source) {
        refii::lua::audit::LuaBytecodeParityResult floor_parity;
        refii::lua::audit::LuaBytecodeParityResult final_parity;
        const LuaReadabilitySummary floor_readability =
            SummarizeLuaReadability(pre_finalize_parity_floor_source);
        const LuaOutputQualitySummary floor_quality =
            SummarizeLuaOutputQuality(pre_finalize_parity_floor_source);
        const bool floor_has_invalid_nil_usage =
            ContainsObviouslyInvalidNilUsage(pre_finalize_parity_floor_source);
        const bool floor_is_publishable_quality =
            !LooksLikeRegisterizedLua(floor_readability) &&
            !HasBlockingLuaOutputQualityIssues(floor_quality) &&
            !floor_has_invalid_nil_usage;
        if (floor_is_publishable_quality &&
            refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                *parity_reference,
                pre_finalize_parity_floor_source,
                output_path.generic_string(),
                &floor_parity) &&
            refii::lua::audit::EvaluateLuaSourceBytecodeParity(*parity_reference,
                                                               final_source,
                                                               output_path.generic_string(),
                                                               &final_parity) &&
            !final_parity.exact_bytecode &&
            IsParityResultImproved(floor_parity, final_parity) &&
            validate_candidate_source(pre_finalize_parity_floor_source,
                                      output_path.generic_string())
                .empty()) {
            final_source = pre_finalize_parity_floor_source;
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "validated_output_reverted_finalize_parity_regression");
            }
        }
    }

    LuaReadabilitySummary readability = SummarizeLuaReadability(final_source);
    LuaOutputQualitySummary quality = SummarizeLuaOutputQuality(final_source);
    bool has_invalid_nil_usage = ContainsObviouslyInvalidNilUsage(final_source);
    const bool allow_validated_native_raw =
        note != nullptr && HasValidatedNativeRawNote(*note);
    const bool bypass_quality_for_exact_parity = require_exact_parity;
    const bool allow_small_holdout_native_quality_relaxation =
        HasScriptPolicyFlag(quality_scope_path, refii::lua::LuaScriptProfileFlag::kScopedSmallHoldout);
    const bool can_relax_small_holdout_quality =
        allow_small_holdout_native_quality_relaxation &&
        IsConservativeSmallHoldoutQualityRelaxCandidate(readability);
    const bool allow_gameface_quality_relaxation =
        HasScriptPolicyFlag(quality_scope_path, refii::lua::LuaScriptProfileFlag::kGameface);
    const bool can_relax_gameface_quality =
        allow_gameface_quality_relaxation &&
        IsConservativeGamefaceQualityRelaxCandidate(readability);
    const bool allow_behavior_terminal_quality_relaxation =
        finalize_output_policy.runtime_behavior_path;
    const bool can_relax_behavior_terminal_quality =
        allow_behavior_terminal_quality_relaxation &&
        IsConservativeBehaviorTerminalQualityRelaxCandidate(readability);
    const bool allow_entity_mode_quality_relaxation = HasScriptPolicyFlag(
        quality_scope_path, refii::lua::LuaScriptProfileFlag::kEntityModeScriptPath);
    const bool can_relax_entity_mode_quality =
        allow_entity_mode_quality_relaxation && readability.nonempty_line_count >= 600 &&
        readability.temporary_self_reference_assignment_count == 0 &&
        readability.multi_temporary_token_line_count * 3 < readability.nonempty_line_count;
    const bool allow_disassembly_recovery_quality_relaxation = HasScriptPolicyFlag(
        quality_scope_path, refii::lua::LuaScriptProfileFlag::kDisassemblyRecoveryPriority);
    const bool can_relax_disassembly_recovery_quality =
        allow_disassembly_recovery_quality_relaxation;
    if (!bypass_quality_for_exact_parity && has_invalid_nil_usage) {
        bool normalized_invalid_nil = false;
        normalized_invalid_nil |=
            TryNormalizeDisassemblyRecoveryInvalidNilArithmetic(final_source, note);
        normalized_invalid_nil |= TryNormalizeInvalidNilCallExpressions(final_source, note);
        if (normalized_invalid_nil) {
        readability = SummarizeLuaReadability(final_source);
        quality = SummarizeLuaOutputQuality(final_source);
        has_invalid_nil_usage = ContainsObviouslyInvalidNilUsage(final_source);
        if (!has_invalid_nil_usage && note != nullptr) {
            *note = JoinNotes(
                *note,
                allow_disassembly_recovery_quality_relaxation
                    ? "quality_relaxed_disassembly_recovery_invalid_nil_normalized"
                    : "quality_relaxed_invalid_nil_normalized");
        }
        }
    }
    if (!bypass_quality_for_exact_parity && LooksLikeRegisterizedLua(readability)) {
        if (allow_validated_native_raw) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_bypassed_validated_native_raw=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (IsRunnableLuaDespiteTemporaryNoise(readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_temporary_noise=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_structured_tables=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_large_body=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (can_relax_small_holdout_quality) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_small_holdout=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (can_relax_gameface_quality) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_gameface=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (can_relax_behavior_terminal_quality) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_behavior_terminal=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (can_relax_entity_mode_quality) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_entity_mode=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (can_relax_disassembly_recovery_quality) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_disassembly_recovery=" +
                                      DescribeLuaReadability(readability));
            }
        } else {
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "quality_rejected_registerized_output=" +
                                  DescribeLuaReadability(readability));
        }
        try_write_failed_output();
        return false;
        }
    }

    if (!bypass_quality_for_exact_parity &&
        (HasBlockingLuaOutputQualityIssues(quality) || has_invalid_nil_usage)) {
        if (note != nullptr) {
            if (HasBlockingLuaOutputQualityIssues(quality)) {
                *note = JoinNotes(*note,
                                  "quality_rejected_decompiler_artifacts=" +
                                      DescribeLuaOutputQuality(quality));
            }
            if (has_invalid_nil_usage) {
                *note = JoinNotes(*note, "quality_rejected_invalid_nil_usage");
            }
        }
        try_write_failed_output();
        return false;
    }

    if (note != nullptr) {
        if (bypass_quality_for_exact_parity) {
            *note = JoinNotes(*note, "quality_bypassed_exact_parity");
        }
        if (!bypass_quality_for_exact_parity && !LooksLikeRegisterizedLua(readability)) {
            RemoveNotesWithPrefixes(
                note,
                {"quality_bypassed_validated_native_raw=",
                 "quality_rejected_registerized_output=",
                 "quality_relaxed_temporary_noise=",
                 "quality_relaxed_structured_tables=",
                 "quality_relaxed_large_body=",
                 "quality_relaxed_small_holdout=",
                 "quality_relaxed_behavior_terminal=",
                 "quality_relaxed_entity_mode="});
        }
        if (bypass_quality_for_exact_parity || !HasBlockingLuaOutputQualityIssues(quality)) {
            RemoveNotesWithPrefixes(note, {"quality_rejected_decompiler_artifacts="});
        }
        if (bypass_quality_for_exact_parity || !has_invalid_nil_usage) {
            RemoveNotesWithPrefixes(note, {"quality_rejected_invalid_nil_usage"});
        }
        RemoveNotesWithPrefixes(note, {"failed_output_saved"});
    }

    if (!WriteNormalizedLuaTextFile(output_path, final_source, note)) {
        if (note != nullptr) {
            *note = JoinNotes(*note, "write_decompiled_output_failed");
        }
        return false;
    }
    if (!failed_output_path.empty()) {
        std::error_code remove_error;
        const bool removed = std::filesystem::remove(failed_output_path, remove_error);
        if (removed) {
            if (note != nullptr) {
                *note = JoinNotes(*note, "failed_output_cleared");
            }
        } else if (remove_error && note != nullptr) {
            *note = JoinNotes(*note, "failed_output_clear_failed");
        }
    }
    return true;
}

bool TryApplyTargetedRawInvalidStructuralPreRepair(std::string& source,
                                                   std::string* validation_error,
                                                   std::string_view validation_label,
                                                   std::string* note);
bool IsTargetedRawInvalidStructuralFamily(std::string_view validation_error);
bool TryStripStandaloneEndBeforeTopLevelClassBoundary(std::string& source, std::string* note);

refii::lua::internal::NativeCandidateFinalizeResult TryFinalizeNativeCandidateOutput(
    const std::filesystem::path& relative_input,
    std::string candidate_source,
    const std::vector<char>& input_bytes,
    const std::filesystem::path& output_path,
    const std::filesystem::path& failed_output_path,
    const refii::lua::LuaWorkspaceDecompileOptions* options,
    const std::filesystem::path* input_path,
    std::string* note,
    std::string_view note_prefix) {
    StripLegacyDecompilerBanner(&candidate_source, note);
    TryStripSyntheticRootWrapperSource(&candidate_source, note);
    if (TrimWhitespace(candidate_source).empty() &&
        IsTriviallyEmptyLuaMainChunk(input_bytes)) {
        candidate_source = "\n";
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_empty_stub_body");
        }
    }
    const std::string initial_native_candidate_source = candidate_source;
    refii::lua::internal::NativeCandidateFinalizeResult result;
    result.exact_parity_required = ShouldRequireExactParityForInputPolicy(
        relative_input, input_path, input_bytes, options);
    const bool allow_exhaustive_exact_rescue = ShouldUseExhaustiveRescueForCurrentFile();
    refii::lua::audit::LuaBytecodeParityReference parity_reference;
    if (result.exact_parity_required &&
        !refii::lua::audit::BuildLuaBytecodeParityReference(input_bytes, &parity_reference)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_parity_reference_unavailable");
        }
        return result;
    }

    const LuaReadabilitySummary candidate_readability =
        SummarizeLuaReadability(candidate_source);
    const bool allow_validated_native_raw =
        note != nullptr && HasValidatedNativeRawNote(*note);
    const bool allow_small_holdout_native_quality_relaxation = HasScriptPolicyFlag(
        NormalizePathString(relative_input.generic_string()),
        refii::lua::LuaScriptProfileFlag::kScopedSmallHoldout);
    const bool can_relax_small_holdout_quality =
        allow_small_holdout_native_quality_relaxation &&
        IsConservativeSmallHoldoutQualityRelaxCandidate(candidate_readability);
    const std::string finalize_quality_scope_path =
        NormalizePathString(relative_input.generic_string());
    const bool allow_gameface_quality_relaxation = HasScriptPolicyFlag(
        finalize_quality_scope_path, refii::lua::LuaScriptProfileFlag::kGameface);
    const bool can_relax_gameface_quality =
        allow_gameface_quality_relaxation &&
        IsConservativeGamefaceQualityRelaxCandidate(candidate_readability);
    const bool allow_behavior_terminal_quality_relaxation =
        refii::lua::BuildLuaScriptPolicy(finalize_quality_scope_path).runtime_behavior_path;
    const bool can_relax_behavior_terminal_quality =
        allow_behavior_terminal_quality_relaxation &&
        IsConservativeBehaviorTerminalQualityRelaxCandidate(candidate_readability);
    if (!result.exact_parity_required && LooksLikeRegisterizedLua(candidate_readability)) {
        if (allow_validated_native_raw) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_bypassed_validated_raw=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsRunnableLuaDespiteTemporaryNoise(candidate_readability)) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_relaxed=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(
                       candidate_readability)) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_structured_tables=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
                       candidate_readability)) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_large_body=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (can_relax_small_holdout_quality) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_small_holdout=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (can_relax_gameface_quality) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_gameface=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (can_relax_behavior_terminal_quality) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_behavior_terminal=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_rejected=" +
                                      DescribeLuaReadability(candidate_readability));
            }
            return result;
        }
    }

    const std::string normalized_relative =
        NormalizePathString(relative_input.generic_string());
    refii::lua::LuaScriptPolicy finalize_policy =
        refii::lua::BuildLuaScriptPolicy(normalized_relative);
    if (!finalize_policy.runtime_behavior_path &&
        !finalize_policy.runtime_hotspot &&
        input_path != nullptr && !input_path->empty()) {
        const refii::lua::LuaScriptPolicy input_path_policy =
            refii::lua::BuildLuaScriptPolicy(
                NormalizePathString(input_path->generic_string()));
        if (input_path_policy.runtime_behavior_path ||
            input_path_policy.runtime_hotspot ||
            input_path_policy.runtime_disassembly_recovery_target) {
            finalize_policy = input_path_policy;
        }
    }
    const auto load_finalized_output_source = [&]() {
        std::string finalized_source;
        if (ReadTextFile(output_path, finalized_source) && !finalized_source.empty()) {
            return finalized_source;
        }
        return candidate_source;
    };
    const bool disassembly_recovery_priority_path =
        finalize_policy.runtime_disassembly_recovery_target;
    const bool force_runtime_light_mode_path =
        IsRuntimeLightModePath(finalize_policy, input_bytes.size());
    const bool runtime_heavy_family_clamp =
        ShouldClampRuntimeHeavyRepairFamilies(finalize_policy, input_bytes.size());
    const bool allow_exhaustive_exact_rescue_for_path =
        allow_exhaustive_exact_rescue &&
        !disassembly_recovery_priority_path &&
        !force_runtime_light_mode_path;
    if (force_runtime_light_mode_path && note != nullptr && !note_prefix.empty()) {
        *note = JoinNotes(*note,
                          std::string(note_prefix) +
                              "_validated_exact_repair_runtime_light_mode_hotspot");
    }
    refii::lua::internal::LuaMutableTextContext candidate_text_context(candidate_source);
    const auto validate_candidate_source = [&]() {
        candidate_text_context.SetSourceView(candidate_source);
        return candidate_text_context.ValidateCached(normalized_relative);
    };
    std::string validation_error = validate_candidate_source();
    std::unordered_set<std::string> heavy_repair_family_states;
    const auto allow_heavy_repair_family = [&](std::string_view family) {
        if (runtime_heavy_family_clamp && IsRuntimeHeavyRepairFamily(family)) {
            AppendNoProgressNote(note, std::string(family) + "_runtime_clamped");
            return false;
        }
        return ShouldAttemptRepairFamilyForState(
            &heavy_repair_family_states, family, candidate_source, validation_error, note);
    };
    const auto refresh_candidate_validation = [&]() {
        validation_error = validate_candidate_source();
    };
    const auto refresh_validation_after_quest_owner_cleanup = [&]() -> bool {
        if (!TryNormalizeQuestOwnerContinuationCleanup(candidate_source, note)) {
            return false;
        }
        refresh_candidate_validation();
        return true;
    };
    const auto try_replace_decompiler_error_functions_and_refresh =
        [&](bool normalize_quest_owner_after_replace) -> bool {
        if (!allow_heavy_repair_family("native_replace_decompiler_error_functions")) {
            return false;
        }
        const std::string before_replacement_source = candidate_source;
        if (!TryReplaceDecompilerErrorFunctions(input_bytes,
                                                candidate_source,
                                                note,
                                                false,
                                                options,
                                                input_path) ||
            candidate_source == before_replacement_source) {
            return false;
        }
        refresh_candidate_validation();
        if (normalize_quest_owner_after_replace) {
            refresh_validation_after_quest_owner_cleanup();
        }
        return true;
    };
    const auto try_apply_raw_invalid_structural_baseline_repairs = [&]() -> bool {
        bool changed_this_pass = false;
        if (TryApplyTargetedRawInvalidStructuralPreRepair(
                candidate_source, &validation_error, normalized_relative, note)) {
            changed_this_pass = true;
        }
        if (!validation_error.empty() &&
            TryStripRedundantEndBeforeTopLevelBoundary(candidate_source, note)) {
            changed_this_pass = true;
            refresh_candidate_validation();
        }
        if (!validation_error.empty() &&
            TryStripStandaloneEndBeforeTopLevelClassBoundary(candidate_source, note)) {
            changed_this_pass = true;
            refresh_candidate_validation();
        }
        return changed_this_pass;
    };
    const auto try_write_failed_candidate_output = [&]() {
        if (failed_output_path.empty()) {
            return;
        }
        std::filesystem::create_directories(failed_output_path.parent_path());
        if (WriteNormalizedLuaTextFile(failed_output_path, candidate_source, note)) {
            if (note != nullptr) {
                *note = JoinNotes(*note, "failed_output_saved");
            }
        } else if (note != nullptr) {
            *note = JoinNotes(*note, "failed_output_save_failed");
        }
    };
    if (!validation_error.empty()) {
        TryApplyTargetedRawInvalidStructuralPreRepair(
            candidate_source, &validation_error, normalized_relative, note);
    }
    const std::string initially_validated_candidate_source = candidate_source;
    const bool candidate_initially_valid = validation_error.empty();
    const auto try_iterative_bytecode_function_refinement = [&]() {
        if (result.exact_parity_required || validation_error.empty()) {
            return false;
        }

        const bool likely_top_level_boundary_bleed =
            IsUnclosedConstructValidationFamily(validation_error) &&
            candidate_source.find("= function(") != std::string::npos;
        int max_passes = likely_top_level_boundary_bleed
                             ? GetCurrentRepairAttemptCap(24, 8)
                             : GetCurrentRepairAttemptCap(8, 4);
        if (force_runtime_light_mode_path) {
            max_passes = std::min(max_passes, GetCurrentRepairAttemptCap(4, 2));
        } else if (runtime_heavy_family_clamp) {
            max_passes = std::min(max_passes, GetCurrentRepairAttemptCap(6, 3));
        }
        bool changed_any = false;
        std::string previous_state;
        changed_any |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
            const std::string before_state =
                BuildRepairQualityStateKey(candidate_source, validation_error);
            if (!previous_state.empty() && before_state == previous_state) {
                AppendNoProgressNote(
                    note,
                    std::string(note_prefix) +
                        "_bytecode_function_refinement_no_progress");
                return false;
            }

            bool changed_this_pass = false;
                if (allow_heavy_repair_family("bytecode_function_refinement") &&
                    TryRepairReportedFunctionsFromBytecodeIteratively(
                        input_bytes,
                        candidate_source,
                        &validation_error,
                        note,
                        std::string(note_prefix) +
                            "_bytecode_function_refinement")) {
                    changed_this_pass = true;
                    refresh_validation_after_quest_owner_cleanup();
                    if (validation_error.empty()) {
                        changed_any = true;
                        return false;
                    }
                }

                if (try_replace_decompiler_error_functions_and_refresh(true)) {
                    changed_this_pass = true;
                    if (validation_error.empty()) {
                        changed_any = true;
                        return false;
                    }
                }

            if (!changed_this_pass) {
                return false;
            }

            changed_any = true;
            const std::string after_state =
                BuildRepairQualityStateKey(candidate_source, validation_error);
            if (after_state == before_state || after_state == previous_state) {
                AppendNoProgressNote(
                    note,
                    std::string(note_prefix) +
                        "_bytecode_function_refinement_no_progress");
                return false;
            }
            previous_state = std::move(after_state);
            return true;
        });

        return changed_any;
    };
    refii::lua::audit::LuaBytecodeParityResult parity_result;
    if (!result.exact_parity_required) {
        if (allow_heavy_repair_family("native_candidate_repair_initial")) {
            TryRepairNativeCandidateSource(candidate_source,
                                           &input_bytes,
                                           note,
                                           note_prefix,
                                           &validation_error);
        }
        if (!validation_error.empty()) {
            try_iterative_bytecode_function_refinement();
        }
        if (validation_error.empty()) {
            std::string previous_source;
            const int native_parity_pass_cap = GetCurrentRepairAttemptCap(3, 2);
            RunBoundedNormalizationPassLoop(native_parity_pass_cap, [&](int /*pass*/) {
                const std::string before_pass = candidate_source;
                if (!TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                            candidate_source,
                                                            note,
                                                            note_prefix,
                                                            options,
                                                            input_path)) {
                    return false;
                }
                refresh_candidate_validation();
                if (!validation_error.empty()) {
                    return false;
                }
                if (candidate_source == before_pass || candidate_source == previous_source) {
                    return false;
                }
                previous_source = std::move(before_pass);
                return true;
            });
        }
        if (candidate_initially_valid && !validation_error.empty()) {
            candidate_source = initially_validated_candidate_source;
            validation_error.clear();
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_candidate_repair_reverted_invalid");
            }
        }
    }
    const bool skip_exact_parity_probe =
        result.exact_parity_required &&
        ((disassembly_recovery_priority_path && input_bytes.size() >= 96 * 1024) ||
         ((force_runtime_light_mode_path || runtime_heavy_family_clamp) &&
          input_bytes.size() >= 160 * 1024));
    if (result.exact_parity_required && validation_error.empty() &&
        !skip_exact_parity_probe &&
        TryEvaluateExactParityCandidate(parity_reference,
                                        candidate_source,
                                        normalized_relative,
                                        &parity_result)) {
        goto exact_parity_validated;
    }
    if (result.exact_parity_required && validation_error.empty() &&
        !skip_exact_parity_probe) {
        refii::lua::audit::LuaBytecodeParityResult early_localdiff_parity;
        if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                               candidate_source,
                                                               normalized_relative,
                                                               &early_localdiff_parity) &&
            TryExactifyLocalDiffHeaderParameters(parity_reference,
                                                 normalized_relative,
                                                 &candidate_source,
                                                 &early_localdiff_parity,
                                                 note,
                                                 note_prefix,
                                                 &input_bytes)) {
            parity_result = std::move(early_localdiff_parity);
            goto exact_parity_validated;
        }
    }
    {
        const std::string pre_restore_source = candidate_source;
        if (refii::lua::decompiler::TryApplyBytecodeBackedMainChunkRestorations(
                input_bytes,
                normalized_relative,
                &candidate_source,
                note,
                note_prefix)) {
            refresh_candidate_validation();
            if (!validation_error.empty()) {
                candidate_source = pre_restore_source;
                refresh_candidate_validation();
                if (note != nullptr && !note_prefix.empty()) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_bytecode_restoration_reverted_invalid");
                }
            }
        }
    }
    if (!validation_error.empty() && !result.exact_parity_required) {
        const std::string post_restore_note_prefix =
            note_prefix.empty() ? std::string("post_restore_candidate_repaired")
                                : std::string(note_prefix) + "_post_restore";
        if (allow_heavy_repair_family("post_restore_native_candidate_repair") &&
            TryRepairNativeCandidateSource(candidate_source,
                                           &input_bytes,
                                           note,
                                           post_restore_note_prefix,
                                           &validation_error)) {
            if (!validation_error.empty()) {
                try_iterative_bytecode_function_refinement();
            }
            if (validation_error.empty() &&
                TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                       candidate_source,
                                                       note,
                                                       post_restore_note_prefix,
                                                       options,
                                                       input_path)) {
                refresh_candidate_validation();
            }
        }
    }
    if (!validation_error.empty() && result.exact_parity_required) {
        const bool targeted_raw_invalid_family =
            IsTargetedRawInvalidStructuralFamily(validation_error);
        const bool priority_raw_invalid_path =
            finalize_policy.profile.Has(refii::lua::LuaScriptProfileFlag::kRawInvalidRecoveryPriority);
        const bool should_attempt_exact_invalid_repair =
            targeted_raw_invalid_family || priority_raw_invalid_path;
        const std::string exact_invalid_repair_note_prefix =
            note_prefix.empty() ? std::string("exact_invalid_repair")
                                : std::string(note_prefix) + "_exact_invalid_repair";
        const bool runtime_light_invalid_path =
            force_runtime_light_mode_path ||
            finalize_policy.profile.Has(refii::lua::LuaScriptProfileFlag::kCameraScriptPath) ||
            finalize_policy.profile.Has(refii::lua::LuaScriptProfileFlag::kGameface);
        const bool runtime_risky_raw_invalid_path = finalize_policy.profile.Has(
            refii::lua::LuaScriptProfileFlag::kBehaviorCultist);
        if (should_attempt_exact_invalid_repair && runtime_light_invalid_path) {
            const int runtime_light_pass_cap = GetCurrentRepairAttemptCap(2, 1);
            RunBoundedNormalizationPassLoop(runtime_light_pass_cap, [&](int /*pass*/) {
                if (validation_error.empty()) {
                    return false;
                }
                const std::string before_quality_state =
                    BuildRepairQualityStateKey(candidate_source, validation_error);
                const bool changed_this_pass =
                    try_apply_raw_invalid_structural_baseline_repairs();
                if (validation_error.empty()) {
                    if (note != nullptr) {
                        *note = JoinNotes(*note,
                                          exact_invalid_repair_note_prefix +
                                              "_runtime_light_validated");
                    }
                    return false;
                }
                if (!changed_this_pass) {
                    return false;
                }
                const std::string after_quality_state =
                    BuildRepairQualityStateKey(candidate_source, validation_error);
                return after_quality_state != before_quality_state;
            });
            if (!validation_error.empty() && note != nullptr) {
                *note = JoinNotes(*note,
                                  exact_invalid_repair_note_prefix +
                                      "_skipped_runtime_light_path");
            }
        } else if (should_attempt_exact_invalid_repair &&
                   !runtime_risky_raw_invalid_path) {
            std::string previous_quality_state;
            const int max_passes = GetCurrentRepairAttemptCap(6, 3);
            RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
                if (validation_error.empty()) {
                    return false;
                }
                const std::string before_quality_state =
                    BuildRepairQualityStateKey(candidate_source, validation_error);
                if (!previous_quality_state.empty() &&
                    before_quality_state == previous_quality_state) {
                    AppendNoProgressNote(note,
                                         exact_invalid_repair_note_prefix +
                                             "_no_progress");
                    return false;
                }

                bool changed_this_pass =
                    try_apply_raw_invalid_structural_baseline_repairs();
                if (!validation_error.empty() &&
                    allow_heavy_repair_family("bytecode_reported_function_refinement") &&
                    TryRepairReportedFunctionsFromBytecodeIteratively(
                        input_bytes,
                        candidate_source,
                        &validation_error,
                        note,
                        exact_invalid_repair_note_prefix + "_reported_function")) {
                    changed_this_pass = true;
                    refresh_validation_after_quest_owner_cleanup();
                }
                if (!validation_error.empty() &&
                    try_replace_decompiler_error_functions_and_refresh(true)) {
                    changed_this_pass = true;
                }
                if (!validation_error.empty()) {
                    if (allow_heavy_repair_family("native_candidate_source_repair") &&
                        TryRepairNativeCandidateSource(candidate_source,
                                                       &input_bytes,
                                                       note,
                                                       exact_invalid_repair_note_prefix,
                                                       &validation_error)) {
                        changed_this_pass = true;
                    }
                }
                if (validation_error.empty()) {
                    if (note != nullptr) {
                        *note = JoinNotes(*note,
                                          exact_invalid_repair_note_prefix +
                                              "_validated");
                    }
                    return false;
                }
                if (!changed_this_pass) {
                    return false;
                }
                const std::string after_quality_state =
                    BuildRepairQualityStateKey(candidate_source, validation_error);
                if (after_quality_state == before_quality_state ||
                    after_quality_state == previous_quality_state) {
                    AppendNoProgressNote(note,
                                         exact_invalid_repair_note_prefix +
                                             "_no_progress");
                    return false;
                }
                previous_quality_state = std::move(after_quality_state);
                return true;
            });
        } else if (should_attempt_exact_invalid_repair &&
                   runtime_risky_raw_invalid_path &&
                   note != nullptr) {
            std::string previous_quality_state;
            const int runtime_risky_repair_pass_cap = GetCurrentRepairAttemptCap(3, 2);
            RunBoundedNormalizationPassLoop(runtime_risky_repair_pass_cap, [&](int /*pass*/) {
                if (validation_error.empty()) {
                    return false;
                }
                const std::string before_quality_state =
                    BuildRepairQualityStateKey(candidate_source, validation_error);
                if (!previous_quality_state.empty() &&
                    before_quality_state == previous_quality_state) {
                    AppendNoProgressNote(note,
                                         exact_invalid_repair_note_prefix +
                                             "_runtime_risky_no_progress");
                    return false;
                }

                bool changed_this_pass =
                    try_apply_raw_invalid_structural_baseline_repairs();
                if (!validation_error.empty()) {
                    if (TryRepairValidatedTopLevelFunctionBoundaryBleed(candidate_source,
                                                                        note)) {
                        changed_this_pass = true;
                        refresh_candidate_validation();
                    }
                    if (!validation_error.empty() &&
                        TryInsertEndBeforeMalformedTopLevelBoundary(candidate_source,
                                                                    validation_error,
                                                                    note)) {
                        changed_this_pass = true;
                        refresh_candidate_validation();
                    }
                    if (TryBalanceOpenBlocks(candidate_source, validation_error, note)) {
                        changed_this_pass = true;
                        refresh_candidate_validation();
                    }
                    if (!validation_error.empty() &&
                        TryAppendMissingEnds(candidate_source, validation_error, note)) {
                        changed_this_pass = true;
                        refresh_candidate_validation();
                    }
                }
                if (!changed_this_pass) {
                    return false;
                }
                if (validation_error.empty()) {
                    *note = JoinNotes(*note,
                                      exact_invalid_repair_note_prefix +
                                          "_runtime_risky_validated");
                    return false;
                }
                const std::string after_quality_state =
                    BuildRepairQualityStateKey(candidate_source, validation_error);
                if (after_quality_state == before_quality_state ||
                    after_quality_state == previous_quality_state) {
                    AppendNoProgressNote(note,
                                         exact_invalid_repair_note_prefix +
                                             "_runtime_risky_no_progress");
                    return false;
                }
                previous_quality_state = std::move(after_quality_state);
                return true;
            });
            if (!validation_error.empty()) {
                *note = JoinNotes(*note,
                                  exact_invalid_repair_note_prefix +
                                      "_skipped_runtime_risky_path");
            }
        }
    }
    if (!validation_error.empty()) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_invalid=" +
                                  SummarizeSyntaxError(validation_error));
        }
        try_write_failed_candidate_output();
        return result;
    }
    if (result.exact_parity_required &&
        (skip_exact_parity_probe ||
         !TryEvaluateExactParityCandidate(parity_reference,
                                          candidate_source,
                                          normalized_relative,
                                          &parity_result))) {
        if (skip_exact_parity_probe) {
            bool parity_classified = false;
            refii::lua::audit::LuaBytecodeParityResult skipped_probe_parity_result;
            if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                                   candidate_source,
                                                                   normalized_relative,
                                                                   &skipped_probe_parity_result)) {
                parity_result = skipped_probe_parity_result;
                parity_classified = true;
                if (parity_result.exact_bytecode) {
                    goto exact_parity_validated;
                }
            }
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(
                    *note,
                    std::string(note_prefix) +
                        "_parity_probe_skipped_disassembly_priority");
                if (force_runtime_light_mode_path) {
                    *note = JoinNotes(
                        *note,
                        std::string(note_prefix) +
                            "_parity_probe_skipped_runtime_light_mode");
                } else if (runtime_heavy_family_clamp) {
                    *note = JoinNotes(
                        *note,
                        std::string(note_prefix) +
                            "_parity_probe_skipped_runtime_heavy_clamp");
                }
                if (parity_classified) {
                    AppendParityClassificationNote(note, note_prefix, parity_result);
                } else {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_parity_probe_unclassified");
                }
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_validated_nonexact");
            }
            if (!FinalizeValidatedLuaOutput(candidate_source,
                                            input_bytes,
                                            options,
                                            input_path,
                                            output_path,
                                            failed_output_path,
                                            note,
                                            result.exact_parity_required ? &parity_reference
                                                                         : nullptr,
                                            false)) {
                if (note != nullptr && !note_prefix.empty()) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_nonexact_rejected");
                }
                return result;
            }
            refii::lua::audit::LuaBytecodeParityResult finalized_output_parity_result;
            std::string finalized_output_source = load_finalized_output_source();
            if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                    parity_reference,
                    finalized_output_source,
                    normalized_relative,
                    &finalized_output_parity_result)) {
                parity_result = finalized_output_parity_result;
                if (finalized_output_parity_result.exact_bytecode) {
                    candidate_source = std::move(finalized_output_source);
                    if (note != nullptr && !note_prefix.empty()) {
                        *note = JoinNotes(*note,
                                          std::string(note_prefix) +
                                              "_validated_nonexact_promoted_post_finalize");
                    }
                    goto exact_parity_validated;
                }
            }
            result.kind = refii::lua::internal::NativeCandidateFinalizeKind::kNonexact;
            return result;
        }
        const auto try_exactify_local_diff_header_parameters =
            [&](std::string* source,
                refii::lua::audit::LuaBytecodeParityResult* parity_out,
                std::string_view local_note_prefix) -> bool {
                return TryExactifyLocalDiffHeaderParameters(parity_reference,
                                                            normalized_relative,
                                                            source,
                                                            parity_out,
                                                            note,
                                                            local_note_prefix,
                                                            &input_bytes);
            };
        if (try_exactify_local_diff_header_parameters(&candidate_source,
                                                      &parity_result,
                                                      note_prefix)) {
            goto exact_parity_validated;
        }
            if (candidate_initially_valid &&
                candidate_source != initially_validated_candidate_source) {
                std::string baseline_candidate_source = initially_validated_candidate_source;
                refii::lua::audit::LuaBytecodeParityResult baseline_parity_result;
                if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                    parity_reference,
                    baseline_candidate_source,
                    normalized_relative,
                    &baseline_parity_result) &&
                try_exactify_local_diff_header_parameters(&baseline_candidate_source,
                                                          &baseline_parity_result,
                                                          note_prefix)) {
                candidate_source = std::move(baseline_candidate_source);
                parity_result = std::move(baseline_parity_result);
                goto exact_parity_validated;
            }
        }
        const bool runtime_watchdog_fast_nonexact =
            force_runtime_light_mode_path &&
            (input_bytes.size() >= 96 * 1024 || candidate_source.size() >= 160 * 1024) &&
            !IsNearExactParityMismatch(parity_result);
        if (runtime_watchdog_fast_nonexact) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(
                    *note,
                    std::string(note_prefix) +
                        "_validated_exact_repair_runtime_watchdog_fast_nonexact");
                AppendParityClassificationNote(note, note_prefix, parity_result);
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_validated_nonexact");
            }
            if (!FinalizeValidatedLuaOutput(candidate_source,
                                            input_bytes,
                                            options,
                                            input_path,
                                            output_path,
                                            failed_output_path,
                                            note,
                                            result.exact_parity_required ? &parity_reference
                                                                         : nullptr,
                                            false)) {
                if (note != nullptr && !note_prefix.empty()) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_nonexact_rejected");
                }
                return result;
            }
            refii::lua::audit::LuaBytecodeParityResult finalized_output_parity_result;
            std::string finalized_output_source = load_finalized_output_source();
            if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                    parity_reference,
                    finalized_output_source,
                    normalized_relative,
                    &finalized_output_parity_result)) {
                parity_result = finalized_output_parity_result;
                if (finalized_output_parity_result.exact_bytecode) {
                    candidate_source = std::move(finalized_output_source);
                    if (note != nullptr && !note_prefix.empty()) {
                        *note = JoinNotes(*note,
                                          std::string(note_prefix) +
                                              "_validated_nonexact_promoted_post_finalize");
                    }
                    goto exact_parity_validated;
                }
            }
            result.kind = refii::lua::internal::NativeCandidateFinalizeKind::kNonexact;
            return result;
        }
        const bool tiny_instruction_only_mismatch_pre =
            IsTinyInstructionOnlyParityMismatch(parity_result);
        const bool near_exact_parity_mismatch_pre = IsNearExactParityMismatch(parity_result);
        const bool tiny_instruction_or_local_only_mismatch_pre =
            IsTinyInstructionOrLocalParityMismatch(parity_result);
        const long long parity_delta_sum_pre = SumFirstMismatchDelta(parity_result);
        const bool large_runtime_risk_candidate_pre =
            input_bytes.size() >= 64 * 1024 || candidate_source.size() >= 96 * 1024;
        const bool skip_rendered_exact_normalization_runtime_guard =
            large_runtime_risk_candidate_pre &&
            !near_exact_parity_mismatch_pre &&
            !tiny_instruction_or_local_only_mismatch_pre;
        const bool should_try_rendered_exact_normalization_pre =
            tiny_instruction_only_mismatch_pre ||
            near_exact_parity_mismatch_pre ||
            tiny_instruction_or_local_only_mismatch_pre;
        if (!skip_rendered_exact_normalization_runtime_guard &&
            should_try_rendered_exact_normalization_pre &&
            TryApplyRenderedExactParityNormalizationCandidate(parity_reference,
                                                              normalized_relative,
                                                              &candidate_source,
                                                              note,
                                                              note_prefix)) {
            goto exact_parity_validated;
        }
        const std::string validated_runtime_state_key_pre =
            BuildValidatedExactRepairRuntimeStateKey(parity_result, candidate_source);
        const std::string validated_runtime_signature_key_pre =
            BuildValidatedExactRepairRuntimeSignatureKey(parity_result);
        const bool repeated_runtime_state_pre =
            note != nullptr && !validated_runtime_state_key_pre.empty() &&
            note->find("validated_exact_repair_state@" + validated_runtime_state_key_pre) !=
                std::string::npos;
        const bool repeated_runtime_signature_pre =
            note != nullptr &&
            !validated_runtime_signature_key_pre.empty() &&
            note->find("validated_exact_repair_sig@" +
                           validated_runtime_signature_key_pre) !=
                std::string::npos;
        const bool skip_heavy_validated_cleanup_runtime_guard =
            force_runtime_light_mode_path ||
            runtime_heavy_family_clamp ||
            repeated_runtime_state_pre ||
            repeated_runtime_signature_pre ||
            (large_runtime_risk_candidate_pre && !near_exact_parity_mismatch_pre &&
             parity_delta_sum_pre > 8);
        if (!tiny_instruction_or_local_only_mismatch_pre) {
            if (skip_heavy_validated_cleanup_runtime_guard) {
                if (note != nullptr && !note_prefix.empty()) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_parity_cleanup_skipped_runtime_guard");
                    if (repeated_runtime_state_pre) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_validated_parity_cleanup_skipped_runtime_guard_repeated_state");
                    }
                    if (repeated_runtime_signature_pre) {
                        *note =
                            JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_parity_cleanup_skipped_runtime_guard_repeated_signature");
                    }
                }
            } else {
                if (TryApplyValidatedParityImprovingCleanupCandidate(&candidate_source,
                                                                     input_bytes,
                                                                     parity_reference,
                                                                     output_path,
                                                                     note,
                                                                     note_prefix) &&
                    TryEvaluateExactParityCandidate(parity_reference,
                                                    candidate_source,
                                                    normalized_relative,
                                                    &parity_result)) {
                    goto exact_parity_validated;
                }
            }
        }
        const bool tiny_instruction_or_local_only_mismatch_post =
            IsTinyInstructionOrLocalParityMismatch(parity_result);
        const bool near_exact_parity_mismatch_post = IsNearExactParityMismatch(parity_result);
        if (!skip_rendered_exact_normalization_runtime_guard &&
            (tiny_instruction_only_mismatch_pre ||
             tiny_instruction_or_local_only_mismatch_post ||
             near_exact_parity_mismatch_post) &&
            TryApplyRenderedExactParityNormalizationCandidate(parity_reference,
                                                              normalized_relative,
                                                              &candidate_source,
                                                              note,
                                                              note_prefix)) {
            goto exact_parity_validated;
        }
        const bool near_exact_parity_mismatch = IsNearExactParityMismatch(parity_result);
        const bool tiny_instruction_or_local_only_mismatch =
            tiny_instruction_or_local_only_mismatch_pre;
        const bool quest_path =
            finalize_policy.profile.Has(refii::lua::LuaScriptProfileFlag::kQuest);
        const bool runtime_or_quest_large_hotspot =
            (finalize_policy.runtime_hotspot || quest_path) &&
            (input_bytes.size() >= 96 * 1024 || candidate_source.size() >= 160 * 1024);
        const bool behavior_tiny_runtime_guard =
            finalize_policy.profile.Has(refii::lua::LuaScriptProfileFlag::kBehavior) &&
            tiny_instruction_or_local_only_mismatch &&
            (input_bytes.size() >= 96 * 1024 || candidate_source.size() >= 160 * 1024);
        const bool behavior_zero_delta_instruction_diff_guard =
            finalize_policy.profile.Has(refii::lua::LuaScriptProfileFlag::kBehavior) &&
            tiny_instruction_or_local_only_mismatch &&
            parity_result.first_mismatch.available &&
            parity_result.first_mismatch.instruction_delta == 0 &&
            parity_result.first_mismatch.constant_delta == 0 &&
            parity_result.first_mismatch.local_delta == 0;
        const long long parity_delta_sum = SumFirstMismatchDelta(parity_result);
        const bool large_runtime_risk_candidate =
            input_bytes.size() >= 64 * 1024 || candidate_source.size() >= 96 * 1024;
        const bool prior_no_progress_signal =
            note != nullptr && HasValidatedExactRepairNoProgressSignal(*note);
        const bool prior_high_cost_signal =
            note != nullptr && HasValidatedExactRepairHighCostSignal(*note);
        const std::string exact_repair_note_prefix =
            note_prefix.empty() ? std::string("validated_exact_repair")
                                : std::string(note_prefix) + "_validated_exact_repair";
        const std::string validated_runtime_state_key =
            BuildValidatedExactRepairRuntimeStateKey(parity_result, candidate_source);
        const std::string validated_runtime_signature_key =
            BuildValidatedExactRepairRuntimeSignatureKey(parity_result);
        const bool repeated_runtime_state =
            note != nullptr && !validated_runtime_state_key.empty() &&
            note->find(exact_repair_note_prefix + "_state@" + validated_runtime_state_key) !=
                std::string::npos;
        const bool repeated_runtime_signature =
            note != nullptr &&
            !validated_runtime_signature_key.empty() &&
            note->find(exact_repair_note_prefix + "_sig@" +
                           validated_runtime_signature_key) !=
                std::string::npos;
        if (note != nullptr &&
            !validated_runtime_state_key.empty() &&
            !repeated_runtime_state) {
            *note = JoinNotes(*note,
                              exact_repair_note_prefix + "_state@" +
                                  validated_runtime_state_key);
        }
        if (note != nullptr &&
            !validated_runtime_signature_key.empty() &&
            !repeated_runtime_signature) {
            *note = JoinNotes(*note,
                              exact_repair_note_prefix + "_sig@" +
                                  validated_runtime_signature_key);
        }
        const bool allow_behavior_zero_delta_template_repair =
            behavior_zero_delta_instruction_diff_guard &&
            !large_runtime_risk_candidate &&
            !repeated_runtime_state &&
            !repeated_runtime_signature &&
            !prior_no_progress_signal &&
            !prior_high_cost_signal;
        if (allow_behavior_zero_delta_template_repair &&
            TryApplyRenderedParityImprovingNormalizationCandidate(
                parity_reference,
                normalized_relative,
                &candidate_source,
                &parity_result,
                note,
                note_prefix)) {
            if (parity_result.exact_bytecode) {
                goto exact_parity_validated;
            }
        }
        if (behavior_zero_delta_instruction_diff_guard && note != nullptr &&
            !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_validated_exact_repair_behavior_zero_delta_instructiondiff_guard");
        }
        if (tiny_instruction_or_local_only_mismatch &&
            !large_runtime_risk_candidate &&
            !repeated_runtime_state &&
            !repeated_runtime_signature &&
            !behavior_zero_delta_instruction_diff_guard) {
            std::string tiny_bulk_candidate_source = candidate_source;
            refii::lua::audit::LuaBytecodeParityResult tiny_bulk_best_parity_result = parity_result;
            bool tiny_bulk_improved = false;
            for (int step = 0; step < 4; ++step) {
                const std::string before_step_source = tiny_bulk_candidate_source;
                if (!TryRepairNativeCandidateBytecodeParity(
                        input_bytes,
                        tiny_bulk_candidate_source,
                        note,
                        exact_repair_note_prefix + "_tiny_bulk_step" +
                            std::to_string(step + 1),
                        options,
                        input_path)) {
                    break;
                }
                if (tiny_bulk_candidate_source == before_step_source ||
                    !ValidateLuaSource(tiny_bulk_candidate_source, normalized_relative).empty()) {
                    break;
                }
                refii::lua::audit::LuaBytecodeParityResult tiny_bulk_parity_result;
                if (!TryEvaluateExactParityCandidate(parity_reference,
                                                     tiny_bulk_candidate_source,
                                                     normalized_relative,
                                                     &tiny_bulk_parity_result)) {
                    break;
                }
                if (tiny_bulk_parity_result.exact_bytecode) {
                    candidate_source = std::move(tiny_bulk_candidate_source);
                    parity_result = std::move(tiny_bulk_parity_result);
                    goto exact_parity_validated;
                }
                if (!IsTinyPrepassParityImproved(tiny_bulk_parity_result,
                                                 tiny_bulk_best_parity_result)) {
                    break;
                }
                tiny_bulk_best_parity_result = std::move(tiny_bulk_parity_result);
                tiny_bulk_improved = true;
            }
            if (tiny_bulk_improved) {
                candidate_source = std::move(tiny_bulk_candidate_source);
                parity_result = std::move(tiny_bulk_best_parity_result);
                if (note != nullptr && !note_prefix.empty()) {
                    *note = JoinNotes(
                        *note,
                        std::string(note_prefix) +
                            "_validated_exact_repair_tiny_bulk_parity_improved");
                }
            }
        }
        if (allow_exhaustive_exact_rescue_for_path &&
            tiny_instruction_or_local_only_mismatch &&
            !large_runtime_risk_candidate &&
            !repeated_runtime_state &&
            !repeated_runtime_signature &&
            !behavior_zero_delta_instruction_diff_guard) {
            std::string tiny_direct_candidate_source = candidate_source;
            if (TryRepairNativeCandidateBytecodeParity(
                    input_bytes,
                    tiny_direct_candidate_source,
                    note,
                    exact_repair_note_prefix + "_tiny_direct",
                    options,
                    input_path) &&
                tiny_direct_candidate_source != candidate_source &&
                ValidateLuaSource(tiny_direct_candidate_source, normalized_relative).empty()) {
                refii::lua::audit::LuaBytecodeParityResult tiny_direct_parity_result;
                if (TryEvaluateExactParityCandidate(parity_reference,
                                                    tiny_direct_candidate_source,
                                                    normalized_relative,
                                                    &tiny_direct_parity_result)) {
                    if (tiny_direct_parity_result.exact_bytecode) {
                        candidate_source = std::move(tiny_direct_candidate_source);
                        parity_result = std::move(tiny_direct_parity_result);
                        goto exact_parity_validated;
                    }
                    if (IsTinyPrepassParityImproved(tiny_direct_parity_result,
                                                    parity_result)) {
                        candidate_source = std::move(tiny_direct_candidate_source);
                        parity_result = std::move(tiny_direct_parity_result);
                        if (note != nullptr && !note_prefix.empty()) {
                            *note = JoinNotes(
                                *note,
                                std::string(note_prefix) +
                                    "_validated_exact_repair_tiny_direct_parity_improved");
                        }
                    }
                }
            }
        }
        const bool force_skip_exhaustive_exact_rescue_large_mismatch =
            allow_exhaustive_exact_rescue_for_path && large_runtime_risk_candidate &&
            parity_delta_sum > 4 && input_bytes.size() >= 64 * 1024;
        const bool skip_exhaustive_exact_rescue_runtime_guard_note_driven =
            allow_exhaustive_exact_rescue_for_path && large_runtime_risk_candidate &&
            !near_exact_parity_mismatch && parity_delta_sum > 3 &&
            (prior_no_progress_signal || prior_high_cost_signal);
        const bool skip_exhaustive_exact_rescue_runtime_guard_strict =
            allow_exhaustive_exact_rescue_for_path && large_runtime_risk_candidate &&
            !near_exact_parity_mismatch && parity_delta_sum > 6;
        const bool skip_exhaustive_exact_rescue_runtime_guard_repeated_state =
            allow_exhaustive_exact_rescue_for_path && repeated_runtime_state;
        const bool skip_exhaustive_exact_rescue_runtime_guard_repeated_signature =
            allow_exhaustive_exact_rescue_for_path && repeated_runtime_signature;
        const bool skip_exhaustive_exact_rescue_runtime_guard_non_near_large =
            allow_exhaustive_exact_rescue_for_path && large_runtime_risk_candidate &&
            !near_exact_parity_mismatch && parity_delta_sum > 14;
        const bool skip_exhaustive_exact_rescue_runtime_guard =
            force_skip_exhaustive_exact_rescue_large_mismatch ||
            skip_exhaustive_exact_rescue_runtime_guard_note_driven ||
            skip_exhaustive_exact_rescue_runtime_guard_strict ||
            skip_exhaustive_exact_rescue_runtime_guard_repeated_state ||
            skip_exhaustive_exact_rescue_runtime_guard_repeated_signature ||
            skip_exhaustive_exact_rescue_runtime_guard_non_near_large ||
            (allow_exhaustive_exact_rescue_for_path && behavior_tiny_runtime_guard);
        const bool skip_exhaustive_exact_rescue_tiny_mismatch =
            allow_exhaustive_exact_rescue_for_path &&
            tiny_instruction_or_local_only_mismatch &&
            (behavior_zero_delta_instruction_diff_guard ||
             large_runtime_risk_candidate ||
             repeated_runtime_state ||
             repeated_runtime_signature ||
             prior_no_progress_signal ||
             prior_high_cost_signal);
        if (skip_exhaustive_exact_rescue_runtime_guard ||
            skip_exhaustive_exact_rescue_tiny_mismatch) {
            if (skip_exhaustive_exact_rescue_tiny_mismatch) {
                std::string tiny_repaired_candidate_source = candidate_source;
                std::string tiny_best_candidate_source = candidate_source;
                refii::lua::audit::LuaBytecodeParityResult tiny_best_parity_result =
                    parity_result;
                bool tiny_prepass_changed = false;
                const auto try_record_tiny_candidate =
                    [&](const std::string& tiny_candidate_source) -> bool {
                    refii::lua::audit::LuaBytecodeParityResult tiny_parity_result;
                    if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                            parity_reference,
                            tiny_candidate_source,
                            normalized_relative,
                            &tiny_parity_result)) {
                        return false;
                    }
                    if (tiny_parity_result.exact_bytecode) {
                        candidate_source = tiny_candidate_source;
                        parity_result = std::move(tiny_parity_result);
                        return true;
                    }
                    if (IsTinyPrepassParityImproved(tiny_parity_result,
                                                    tiny_best_parity_result)) {
                        tiny_best_candidate_source = tiny_candidate_source;
                        tiny_best_parity_result = std::move(tiny_parity_result);
                        tiny_prepass_changed = true;
                    }
                    return false;
                };
                const auto try_apply_tiny_cleanup_lines =
                    [&](auto&& apply_cleanup,
                        std::string* repaired_source_out) -> bool {
                    refii::lua::internal::LuaMutableTextContext tiny_context(
                        tiny_repaired_candidate_source);
                    std::vector<std::string>* tiny_lines =
                        tiny_context.EnsureMutableLines();
                    if (tiny_lines == nullptr || !apply_cleanup(tiny_lines)) {
                        return false;
                    }
                    tiny_context.MarkLinesMutated();
                    if (!tiny_context.ValidateCached(normalized_relative).empty()) {
                        return false;
                    }
                    if (repaired_source_out != nullptr) {
                        *repaired_source_out =
                            std::string(tiny_context.EnsureSource());
                    }
                    return true;
                };
                {
                    std::string subclass_candidate_source;
                    if (try_apply_tiny_cleanup_lines(
                            [&](std::vector<std::string>* subclass_lines) {
                                bool subclass_changed = false;
                                subclass_changed |=
                                    NormalizeRenderedLuaNearExactCreateSubClassSeeds(
                                        subclass_lines);
                                subclass_changed |=
                                    NormalizeRenderedLuaNearExactCreateSubClassSeedsFallback(
                                        subclass_lines);
                                return subclass_changed;
                            },
                            &subclass_candidate_source)) {
                        tiny_prepass_changed = true;
                        if (try_record_tiny_candidate(subclass_candidate_source)) {
                            goto exact_parity_validated;
                        }
                    }
                }
                {
                    std::string direct_candidate_source;
                    if (try_apply_tiny_cleanup_lines(
                            [&](std::vector<std::string>* tiny_lines) {
                                bool direct_changed = false;
                                direct_changed |=
                                    ApplyRenderedLuaNearExactSingletonSeedCluster(
                                        tiny_lines, false, true);
                                direct_changed |=
                                    ApplyRenderedLuaNearExactInstructionCleanupBundle(
                                        tiny_lines, true, true, true);
                                return direct_changed;
                            },
                            &direct_candidate_source)) {
                        const std::string previous_best_candidate_source =
                            tiny_best_candidate_source;
                        tiny_repaired_candidate_source = direct_candidate_source;
                        tiny_prepass_changed = true;
                        if (try_record_tiny_candidate(tiny_repaired_candidate_source)) {
                            goto exact_parity_validated;
                        }
                        if (tiny_best_candidate_source ==
                            previous_best_candidate_source) {
                            tiny_repaired_candidate_source = candidate_source;
                        }
                    }
                }
                const bool allow_tiny_prepass_bytecode_steps =
                    !behavior_zero_delta_instruction_diff_guard &&
                    (!large_runtime_risk_candidate || near_exact_parity_mismatch);
                const int tiny_prepass_max_steps =
                    allow_tiny_prepass_bytecode_steps
                        ? (runtime_or_quest_large_hotspot ? 2 : 6)
                        : 1;
                int tiny_prepass_non_improving_steps = 0;
                for (int step = 0; step < tiny_prepass_max_steps; ++step) {
                    const std::string before_step_source = tiny_repaired_candidate_source;
                    if (!TryRepairNativeCandidateBytecodeParity(
                            input_bytes,
                            tiny_repaired_candidate_source,
                            note,
                            exact_repair_note_prefix + "_tiny_prepass_step" +
                                std::to_string(step + 1),
                            options,
                            input_path)) {
                        break;
                    }
                    if (tiny_repaired_candidate_source == before_step_source) {
                        break;
                    }
                    if (!ValidateLuaSource(tiny_repaired_candidate_source,
                                           normalized_relative)
                             .empty()) {
                        break;
                    }
                    tiny_prepass_changed = true;
                    const std::string previous_best_candidate_source =
                        tiny_best_candidate_source;
                    if (try_record_tiny_candidate(tiny_repaired_candidate_source)) {
                        goto exact_parity_validated;
                    }
                    if (tiny_best_candidate_source == previous_best_candidate_source) {
                        tiny_repaired_candidate_source = before_step_source;
                        ++tiny_prepass_non_improving_steps;
                        if (tiny_prepass_non_improving_steps >= 2) {
                            break;
                        }
                    } else {
                        tiny_prepass_non_improving_steps = 0;
                    }
                }
                if (tiny_prepass_changed &&
                    tiny_best_candidate_source != candidate_source) {
                    candidate_source = std::move(tiny_best_candidate_source);
                    parity_result = std::move(tiny_best_parity_result);
                    if (note != nullptr && !note_prefix.empty()) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_validated_exact_repair_tiny_prepass_parity_improved");
                    }
                }
                if (note != nullptr && !note_prefix.empty()) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          (tiny_prepass_changed
                                               ? "_validated_exact_repair_tiny_prepass_nonexact"
                                               : "_validated_exact_repair_tiny_prepass_no_change"));
                    if (!allow_tiny_prepass_bytecode_steps) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_validated_exact_repair_tiny_prepass_runtime_bounded");
                    }
                }
            }
            if (note != nullptr && !note_prefix.empty()) {
                if (skip_exhaustive_exact_rescue_tiny_mismatch) {
                    if (behavior_zero_delta_instruction_diff_guard) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_validated_exact_repair_skipped_behavior_instructiondiff_zero_delta");
                    }
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_exact_repair_skipped_tiny_instruction_mismatch");
                } else {
                    if (force_skip_exhaustive_exact_rescue_large_mismatch) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_validated_exact_repair_skipped_runtime_guard_large_mismatch");
                    }
                    if (skip_exhaustive_exact_rescue_runtime_guard_strict) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_validated_exact_repair_skipped_runtime_guard_strict_large_mismatch");
                    }
                    if (skip_exhaustive_exact_rescue_runtime_guard_non_near_large) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_validated_exact_repair_skipped_runtime_guard_non_near_large_mismatch");
                    }
                    if (skip_exhaustive_exact_rescue_runtime_guard_repeated_state) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_validated_exact_repair_skipped_runtime_guard_repeated_state");
                    }
                    if (skip_exhaustive_exact_rescue_runtime_guard_repeated_signature) {
                        *note =
                            JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_exact_repair_skipped_runtime_guard_repeated_signature");
                    }
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_exact_repair_skipped_runtime_guard");
                }
            }
        } else if (allow_exhaustive_exact_rescue_for_path) {
            std::string repaired_candidate_source = candidate_source;
            std::string preferred_nonexact_candidate_source;
            refii::lua::audit::LuaBytecodeParityResult preferred_nonexact_parity_result;
            bool have_preferred_nonexact_candidate = false;
            const auto consider_nonexact_candidate =
                [&](const std::string& nonexact_candidate_source,
                    const refii::lua::audit::LuaBytecodeParityResult& nonexact_parity_result,
                    std::string_view note_suffix) {
                    const std::string_view baseline_source =
                        have_preferred_nonexact_candidate
                            ? std::string_view(preferred_nonexact_candidate_source)
                            : std::string_view(candidate_source);
                    const refii::lua::audit::LuaBytecodeParityResult& baseline_parity =
                        have_preferred_nonexact_candidate ? preferred_nonexact_parity_result
                                                          : parity_result;
                    if (HasScriptPolicyFlag(
                            normalized_relative,
                            refii::lua::LuaScriptProfileFlag::kInteractiveCutsceneActions) &&
                        IsInteractiveCutsceneActionsNearExactF2Mismatch(nonexact_parity_result) &&
                        IsInteractiveCutsceneActionsLargePostF2Mismatch(baseline_parity)) {
                        preferred_nonexact_candidate_source = nonexact_candidate_source;
                        preferred_nonexact_parity_result = nonexact_parity_result;
                        have_preferred_nonexact_candidate = true;
                        if (note != nullptr) {
                            *note = JoinNotes(
                                *note,
                                exact_repair_note_prefix +
                                    "_preferred_nonexact_near_exact_f2_actions");
                        }
                        return;
                    }
                    if (HasScriptPolicyFlag(
                            normalized_relative,
                            refii::lua::LuaScriptProfileFlag::kInteractiveCutsceneActions) &&
                        IsInteractiveCutsceneActionsMismatchEscalation(nonexact_parity_result,
                                                                        baseline_parity)) {
                        if (note != nullptr) {
                            *note = JoinNotes(
                                *note,
                                exact_repair_note_prefix +
                                    "_rejected_nonexact_mismatch_escalation_actions");
                        }
                        return;
                    }
                    std::string preference_reason;
                    if (!ShouldPreferValidatedNonexactCandidate(nonexact_candidate_source,
                                                                nonexact_parity_result,
                                                                baseline_source,
                                                                baseline_parity,
                                                                &preference_reason)) {
                        return;
                    }

                    preferred_nonexact_candidate_source = nonexact_candidate_source;
                    preferred_nonexact_parity_result = nonexact_parity_result;
                    have_preferred_nonexact_candidate = true;
                    if (note != nullptr) {
                        std::string preferred_note =
                            exact_repair_note_prefix + "_" + std::string(note_suffix);
                        if (!preference_reason.empty()) {
                            preferred_note += "_preferred_by_" + preference_reason;
                        }
                        *note = JoinNotes(*note, preferred_note);
                    }
                };
            std::string raw_repair_source = candidate_source;
            const bool skip_raw_repair_runtime_hotspot_clamp =
                runtime_or_quest_large_hotspot &&
                parity_delta_sum >= 2;
            const bool skip_raw_repair_runtime_guard =
                skip_raw_repair_runtime_hotspot_clamp ||
                repeated_runtime_state ||
                (large_runtime_risk_candidate && !near_exact_parity_mismatch &&
                 parity_delta_sum > 3 &&
                 (prior_no_progress_signal || prior_high_cost_signal));
            const bool skip_raw_repair_non_near_large_mismatch =
                large_runtime_risk_candidate && !near_exact_parity_mismatch &&
                parity_delta_sum > 14;
            const bool skip_raw_repair_tiny_mismatch =
                tiny_instruction_or_local_only_mismatch &&
                (large_runtime_risk_candidate ||
                 behavior_tiny_runtime_guard ||
                 repeated_runtime_state ||
                 prior_no_progress_signal ||
                 prior_high_cost_signal);
            const bool should_render_raw_donor =
                !skip_raw_repair_runtime_guard &&
                !skip_raw_repair_non_near_large_mismatch &&
                !skip_raw_repair_tiny_mismatch &&
                refii::lua::decompiler::CouldValidatedCandidateBenefitFromRawRepair(
                    candidate_source);
            if (should_render_raw_donor) {
                TryRenderNativeMainChunkCandidate(input_bytes,
                                                  &raw_repair_source,
                                                  note,
                                                  false,
                                                  exact_repair_note_prefix + "_raw_donor",
                                                  normalized_relative);
            } else if (skip_raw_repair_tiny_mismatch && note != nullptr) {
                *note = JoinNotes(*note,
                                  exact_repair_note_prefix +
                                      "_raw_donor_skipped_tiny_instruction_mismatch");
            } else if (skip_raw_repair_runtime_guard && note != nullptr) {
                *note = JoinNotes(*note,
                                  exact_repair_note_prefix +
                                      "_raw_donor_skipped_runtime_guard");
                if (skip_raw_repair_runtime_hotspot_clamp) {
                    *note = JoinNotes(*note,
                                      exact_repair_note_prefix +
                                          "_raw_donor_skipped_runtime_hotspot_clamp");
                }
            } else if (skip_raw_repair_non_near_large_mismatch && note != nullptr) {
                *note = JoinNotes(*note,
                                  exact_repair_note_prefix +
                                      "_raw_donor_skipped_non_near_large_mismatch");
            } else if (note != nullptr) {
                *note = JoinNotes(*note,
                                  exact_repair_note_prefix +
                                      "_raw_donor_skipped_clean_candidate");
            }
            if (!skip_raw_repair_runtime_guard &&
                !skip_raw_repair_non_near_large_mismatch &&
                !skip_raw_repair_tiny_mismatch &&
                refii::lua::decompiler::TryRepairValidatedCandidateFromRaw(
                    input_bytes,
                    normalized_relative,
                    raw_repair_source,
                    &repaired_candidate_source,
                    note,
                    exact_repair_note_prefix) &&
                ValidateLuaSource(repaired_candidate_source, normalized_relative).empty()) {
                refii::lua::audit::LuaBytecodeParityResult repaired_parity_result;
                const bool repaired_has_parity =
                    refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                                       repaired_candidate_source,
                                                                       normalized_relative,
                                                                       &repaired_parity_result);
                if (repaired_has_parity && repaired_parity_result.exact_bytecode) {
                    parity_result = std::move(repaired_parity_result);
                    candidate_source = std::move(repaired_candidate_source);
                    goto exact_parity_validated;
                }
                if (repaired_has_parity) {
                    consider_nonexact_candidate(repaired_candidate_source,
                                                repaired_parity_result,
                                                "kept_nonexact_readability_gain");
                }
                if (TryApplyValidatedParityImprovingCleanupCandidate(&repaired_candidate_source,
                                                                     input_bytes,
                                                                     parity_reference,
                                                                     output_path,
                                                                     note,
                                                                     note_prefix) &&
                    ValidateLuaSource(repaired_candidate_source, normalized_relative).empty()) {
                    refii::lua::audit::LuaBytecodeParityResult cleaned_repaired_parity_result;
                    const bool cleaned_repaired_has_parity =
                        refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                            parity_reference,
                            repaired_candidate_source,
                            normalized_relative,
                            &cleaned_repaired_parity_result);
                    if (cleaned_repaired_has_parity &&
                        cleaned_repaired_parity_result.exact_bytecode) {
                        parity_result = std::move(cleaned_repaired_parity_result);
                        candidate_source = std::move(repaired_candidate_source);
                        goto exact_parity_validated;
                    }
                    if (cleaned_repaired_has_parity) {
                        consider_nonexact_candidate(
                            repaired_candidate_source,
                            cleaned_repaired_parity_result,
                            "kept_nonexact_parity_improved_cleanup");
                    }
                }
                if (TryApplyRenderedExactParityNormalizationCandidate(parity_reference,
                                                                      normalized_relative,
                                                                      &repaired_candidate_source,
                                                                      note,
                                                                      note_prefix)) {
                    candidate_source = std::move(repaired_candidate_source);
                    goto exact_parity_validated;
                }
            }
            if (have_preferred_nonexact_candidate) {
                candidate_source = std::move(preferred_nonexact_candidate_source);
                parity_result = std::move(preferred_nonexact_parity_result);
            }
        } else {
            if (validation_error.empty()) {
                refii::lua::audit::LuaBytecodeParityResult light_mode_parity_result;
                const bool light_mode_has_parity =
                    refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        parity_reference,
                        candidate_source,
                        normalized_relative,
                        &light_mode_parity_result);
                const bool light_mode_tiny_instruction_or_local_mismatch =
                    light_mode_has_parity &&
                    IsTinyInstructionOrLocalParityMismatch(light_mode_parity_result);
                const bool light_mode_behavior_zero_delta_instruction_diff =
                    light_mode_tiny_instruction_or_local_mismatch &&
                    HasScriptPolicyFlag(normalized_relative,
                                        refii::lua::LuaScriptProfileFlag::kBehavior) &&
                    light_mode_parity_result.first_mismatch.available &&
                    light_mode_parity_result.first_mismatch.instruction_delta == 0 &&
                    light_mode_parity_result.first_mismatch.constant_delta == 0 &&
                    light_mode_parity_result.first_mismatch.local_delta == 0;
                const bool allow_light_mode_tiny_exactify =
                    light_mode_tiny_instruction_or_local_mismatch &&
                    !light_mode_behavior_zero_delta_instruction_diff &&
                    input_bytes.size() <= 128 * 1024 &&
                    candidate_source.size() <= 192 * 1024;
                if (allow_light_mode_tiny_exactify) {
                    std::string light_mode_candidate_source = candidate_source;
                    refii::lua::audit::LuaBytecodeParityResult best_light_mode_parity_result =
                        light_mode_parity_result;
                    bool light_mode_improved_nonexact = false;
                    for (int attempt = 0; attempt < 2; ++attempt) {
                        const std::string before_attempt_source = light_mode_candidate_source;
                        if (!TryRepairNativeCandidateBytecodeParity(
                                input_bytes,
                                light_mode_candidate_source,
                                note,
                                note_prefix.empty()
                                    ? "validated_exact_light_mode_tiny"
                                    : std::string(note_prefix) +
                                          "_validated_exact_light_mode_tiny",
                                options,
                                input_path)) {
                            break;
                        }
                        if (light_mode_candidate_source == before_attempt_source ||
                            !ValidateLuaSource(light_mode_candidate_source,
                                               normalized_relative)
                                 .empty()) {
                            break;
                        }

                        refii::lua::audit::LuaBytecodeParityResult repaired_light_mode_parity;
                        if (!TryEvaluateExactParityCandidate(parity_reference,
                                                             light_mode_candidate_source,
                                                             normalized_relative,
                                                             &repaired_light_mode_parity)) {
                            break;
                        }
                        if (repaired_light_mode_parity.exact_bytecode) {
                            candidate_source = std::move(light_mode_candidate_source);
                            parity_result = std::move(repaired_light_mode_parity);
                            goto exact_parity_validated;
                        }
                        if (!IsTinyPrepassParityImproved(repaired_light_mode_parity,
                                                         best_light_mode_parity_result)) {
                            break;
                        }
                        best_light_mode_parity_result =
                            std::move(repaired_light_mode_parity);
                        light_mode_improved_nonexact = true;
                    }
                    if (light_mode_improved_nonexact) {
                        candidate_source = std::move(light_mode_candidate_source);
                        parity_result = std::move(best_light_mode_parity_result);
                        if (note != nullptr && !note_prefix.empty()) {
                            *note = JoinNotes(
                                *note,
                                std::string(note_prefix) +
                                    "_validated_exact_repair_light_mode_tiny_parity_improved");
                        }
                    }
                }
            }
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_validated_exact_repair_light_mode");
                if (disassembly_recovery_priority_path) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_exact_repair_disassembly_priority_light_mode");
                }
            }
        }
        {
            const bool had_trailing_newline =
                !candidate_source.empty() && candidate_source.back() == '\n';
            refii::lua::internal::LuaMutableTextContext nonexact_context;
            nonexact_context.SetLines(refii::lua::SplitRenderedLines(candidate_source),
                                      had_trailing_newline);
            std::vector<std::string>* lines = nonexact_context.EnsureMutableLines();
            const bool allow_call_reassignment_self_compare_cleanup =
                finalize_policy.profile.Has(
                    refii::lua::LuaScriptProfileFlag::kInteractiveCutsceneActions);
            const RenderedLuaNonexactLightCleanupResult light_cleanup_result =
                ApplyRenderedLuaNonexactLightCleanup(
                    lines, allow_call_reassignment_self_compare_cleanup);
            if (light_cleanup_result.changed_any() && lines != nullptr) {
                nonexact_context.MarkLinesMutated();
                if (nonexact_context.ValidateCached(normalized_relative).empty()) {
                    candidate_source = std::string(nonexact_context.EnsureSource());
                    AppendRenderedLuaNonexactLightCleanupNotes(
                        light_cleanup_result, note_prefix, note);
                }
            }
        }
        if (result.exact_parity_required) {
            refii::lua::audit::LuaBytecodeParityResult updated_nonexact_parity;
            if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                                   candidate_source,
                                                                   normalized_relative,
                                                                   &updated_nonexact_parity)) {
                parity_result = updated_nonexact_parity;
            }
            if (try_exactify_local_diff_header_parameters(&candidate_source,
                                                          &parity_result,
                                                          note_prefix)) {
                goto exact_parity_validated;
            }
            if (candidate_initially_valid &&
                candidate_source != initially_validated_candidate_source) {
                std::string baseline_candidate_source = initially_validated_candidate_source;
                refii::lua::audit::LuaBytecodeParityResult baseline_parity_result;
                const std::string baseline_note_prefix =
                    note_prefix.empty() ? std::string("validated_exact_baseline")
                                        : std::string(note_prefix) + "_baseline";
                const bool baseline_has_parity =
                    refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        parity_reference,
                        baseline_candidate_source,
                        normalized_relative,
                        &baseline_parity_result);
                if (baseline_has_parity &&
                    try_exactify_local_diff_header_parameters(&baseline_candidate_source,
                                                              &baseline_parity_result,
                                                              baseline_note_prefix)) {
                    candidate_source = std::move(baseline_candidate_source);
                    parity_result = std::move(baseline_parity_result);
                    goto exact_parity_validated;
                }
            }
            if (candidate_source != initial_native_candidate_source) {
                std::string pristine_candidate_source = initial_native_candidate_source;
                refii::lua::audit::LuaBytecodeParityResult pristine_parity_result;
                if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        parity_reference,
                        pristine_candidate_source,
                        normalized_relative,
                        &pristine_parity_result) &&
                    try_exactify_local_diff_header_parameters(&pristine_candidate_source,
                                                              &pristine_parity_result,
                                                              note_prefix)) {
                    candidate_source = std::move(pristine_candidate_source);
                    parity_result = std::move(pristine_parity_result);
                    goto exact_parity_validated;
                }
            }

            const auto try_promote_exact_candidate_from_context =
                [&](refii::lua::internal::LuaMutableTextContext* transformed_context,
                    std::string* exact_source_out,
                    refii::lua::audit::LuaBytecodeParityResult* exact_parity_out)
                -> bool {
                if (transformed_context == nullptr) {
                    return false;
                }
                if (!transformed_context
                         ->ValidateCached(normalized_relative)
                         .empty()) {
                    return false;
                }

                const std::string exact_candidate_source =
                    std::string(transformed_context->EnsureSource());
                refii::lua::audit::LuaBytecodeParityResult exact_parity_result;
                if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        parity_reference,
                        exact_candidate_source,
                        normalized_relative,
                        &exact_parity_result) ||
                    !exact_parity_result.exact_bytecode) {
                    return false;
                }

                if (exact_source_out != nullptr) {
                    *exact_source_out = exact_candidate_source;
                }
                if (exact_parity_out != nullptr) {
                    *exact_parity_out = std::move(exact_parity_result);
                }
                return true;
            };

            const auto try_promote_exact_candidate_from_lines =
                [&](const std::vector<std::string>& transformed_lines,
                    bool had_trailing_newline,
                    std::string* exact_source_out,
                    refii::lua::audit::LuaBytecodeParityResult* exact_parity_out)
                -> bool {
                refii::lua::internal::LuaMutableTextContext transformed_context;
                transformed_context.SetLines(transformed_lines,
                                             had_trailing_newline);
                return try_promote_exact_candidate_from_context(
                    &transformed_context, exact_source_out, exact_parity_out);
            };

            const auto try_exactify_named_parameters_from_bytecode =
                [&](std::string* source,
                    refii::lua::audit::LuaBytecodeParityResult* parity_out,
                    std::string_view local_note_prefix) -> bool {
                    if (source == nullptr || parity_out == nullptr ||
                        parity_out->exact_bytecode ||
                        !parity_out->first_mismatch.available ||
                        std::llabs(parity_out->first_mismatch.constant_delta) != 0 ||
                        std::llabs(parity_out->first_mismatch.local_delta) != 0 ||
                        std::llabs(parity_out->first_mismatch.instruction_delta) > 32) {
                        return false;
                    }

                    refii::lua::internal::LuaMutableTextContext source_context(*source);
                    std::vector<std::string>* lines_ptr = source_context.EnsureMutableLines();
                    if (lines_ptr == nullptr || lines_ptr->empty()) {
                        return false;
                    }
                    const bool had_trailing_newline = source_context.HadTrailingNewline();
                    const std::vector<std::string>& lines = *lines_ptr;
                    const std::vector<DecompiledFunctionSpan> spans =
                        CollectDecompiledFunctionSpans(lines);
                    const std::unordered_map<std::string, DecompiledFunctionSpan> prefix_span_map =
                        BuildAssignedPrefixSpanMap(lines, spans);
                    std::vector<const refii::lua::Lua51Function*> functions;
                    std::vector<std::size_t> function_root_child_indices;
                    AppendLuaFunctionChildrenPreorderWithRootChildIndex(
                        parity_reference.chunk.main,
                        0,
                        &functions,
                        &function_root_child_indices);
                    if (functions.empty()) {
                        return false;
                    }

                    for (std::size_t function_slot = 0; function_slot < functions.size();
                         ++function_slot) {
                        const std::vector<std::string> expected_params =
                            CollectUsefulChunkParameterNames(*functions[function_slot]);
                        if (expected_params.empty()) {
                            continue;
                        }

                        DecompiledFunctionSpan span;
                        const bool have_resolved_span =
                            TryResolveFunctionSpanForIndex(parity_reference.chunk.main,
                                                           function_slot + 1,
                                                           function_root_child_indices[function_slot],
                                                           lines,
                                                           spans,
                                                           &prefix_span_map,
                                                           &span) &&
                            span.start_line <= span.end_line &&
                            span.end_line < lines.size();
                        if (!have_resolved_span) {
                            for (std::size_t header_line = 0; header_line < lines.size();
                                 ++header_line) {
                                if (!refii::lua::IsRenderedFunctionStartLine(lines[header_line])) {
                                    continue;
                                }
                                std::vector<std::string> rendered_params;
                                if (!ParseRenderedFunctionParameterNames(lines[header_line],
                                                                        &rendered_params) ||
                                    rendered_params.size() != expected_params.size()) {
                                    continue;
                                }

                                bool blocked = false;
                                bool changed = false;
                                for (std::size_t i = 0; i < rendered_params.size(); ++i) {
                                    const std::string& rendered_name = rendered_params[i];
                                    const std::string& expected_name = expected_params[i];
                                    if (rendered_name == expected_name) {
                                        continue;
                                    }
                                    if ((!StartsWith(rendered_name, "arg") &&
                                         !refii::lua::IsRenderTemporaryName(rendered_name)) ||
                                        StartsWith(expected_name, "arg") ||
                                        !refii::lua::IsValidLuaIdentifier(expected_name)) {
                                        blocked = true;
                                        break;
                                    }
                                    if (std::find(rendered_params.begin(),
                                                  rendered_params.end(),
                                                  expected_name) != rendered_params.end()) {
                                        blocked = true;
                                        break;
                                    }
                                    changed = true;
                                }
                                if (blocked || !changed) {
                                    continue;
                                }

                                std::string_view stripped =
                                    refii::lua::StripLuaLineComment(lines[header_line]);
                                const std::size_t function_pos = stripped.find("function");
                                const std::size_t open_paren = stripped.find('(', function_pos);
                                const std::size_t close_paren = stripped.rfind(')');
                                if (function_pos == std::string_view::npos ||
                                    open_paren == std::string_view::npos ||
                                    close_paren == std::string_view::npos ||
                                    close_paren <= open_paren) {
                                    continue;
                                }

                                std::string rewritten_param_text;
                                for (std::size_t i = 0; i < expected_params.size(); ++i) {
                                    if (i != 0) {
                                        rewritten_param_text += ", ";
                                    }
                                    rewritten_param_text += expected_params[i];
                                }

                                std::vector<std::string> transformed_lines = lines;
                                transformed_lines[header_line].replace(open_paren + 1,
                                                                       close_paren - open_paren - 1,
                                                                       rewritten_param_text);
                                std::string exact_candidate_source;
                                refii::lua::audit::LuaBytecodeParityResult exact_parity_result;
                                if (!try_promote_exact_candidate_from_lines(
                                        transformed_lines,
                                        had_trailing_newline,
                                        &exact_candidate_source,
                                        &exact_parity_result)) {
                                    continue;
                                }

                                *source = std::move(exact_candidate_source);
                                *parity_out = std::move(exact_parity_result);
                                if (note != nullptr) {
                                    const std::string prefix =
                                        local_note_prefix.empty()
                                            ? std::string("validated_exact")
                                            : std::string(local_note_prefix);
                                    *note = JoinNotes(*note,
                                                      prefix +
                                                          "_named_params_aligned_header_fallback");
                                }
                                return true;
                            }
                            continue;
                        }

                        std::vector<std::string> rendered_params;
                        if (!ParseRenderedFunctionParameterNames(lines[span.start_line],
                                                                &rendered_params) ||
                            rendered_params.size() != expected_params.size()) {
                            continue;
                        }

                        std::unordered_map<std::string, std::string> replacements;
                        bool blocked = false;
                        for (std::size_t i = 0; i < rendered_params.size(); ++i) {
                            const std::string& rendered_name = rendered_params[i];
                            const std::string& expected_name = expected_params[i];
                            if (rendered_name == expected_name) {
                                continue;
                            }
                            if ((!StartsWith(rendered_name, "arg") &&
                                 !refii::lua::IsRenderTemporaryName(rendered_name)) ||
                                StartsWith(expected_name, "arg") ||
                                !refii::lua::IsValidLuaIdentifier(expected_name)) {
                                blocked = true;
                                break;
                            }
                            if (std::find(rendered_params.begin(),
                                          rendered_params.end(),
                                          expected_name) != rendered_params.end()) {
                                blocked = true;
                                break;
                            }
                            replacements.emplace(rendered_name, expected_name);
                        }
                        if (blocked || replacements.empty()) {
                            continue;
                        }

                        std::vector<std::string> transformed_lines = lines;
                        std::vector<std::string> rewritten =
                            std::vector<std::string>(
                                transformed_lines.begin() +
                                    static_cast<std::ptrdiff_t>(span.start_line),
                                transformed_lines.begin() +
                                    static_cast<std::ptrdiff_t>(span.end_line + 1));
                        for (std::size_t line_index = 0; line_index < rewritten.size();
                             ++line_index) {
                            if (line_index != 0 &&
                                refii::lua::IsRenderedFunctionStartLine(rewritten[line_index])) {
                                const std::size_t nested_end =
                                    refii::lua::FindRenderedFunctionBlockEnd(rewritten,
                                                                             line_index);
                                if (nested_end >= rewritten.size() ||
                                    nested_end <= line_index) {
                                    blocked = true;
                                    break;
                                }
                                line_index = nested_end;
                                continue;
                            }
                            rewritten[line_index] =
                                RewriteLuaIdentifiersWithMap(rewritten[line_index],
                                                             replacements);
                        }
                        if (blocked) {
                            continue;
                        }

                        std::copy(rewritten.begin(),
                                  rewritten.end(),
                                  transformed_lines.begin() +
                                      static_cast<std::ptrdiff_t>(span.start_line));

                        std::string exact_candidate_source;
                        refii::lua::audit::LuaBytecodeParityResult exact_parity_result;
                        if (try_promote_exact_candidate_from_lines(
                                transformed_lines,
                                had_trailing_newline,
                                &exact_candidate_source,
                                &exact_parity_result)) {
                            *source = std::move(exact_candidate_source);
                            *parity_out = std::move(exact_parity_result);
                            if (note != nullptr) {
                                const std::string prefix =
                                    local_note_prefix.empty() ? std::string("validated_exact")
                                                              : std::string(local_note_prefix);
                                *note = JoinNotes(*note, prefix + "_named_params_aligned");
                            }
                            return true;
                        }
                        refii::lua::internal::LuaMutableTextContext restored_context;
                        restored_context.SetLines(transformed_lines, had_trailing_newline);
                        std::string restored_candidate =
                            std::string(restored_context.EnsureSource());
                        if (refii::lua::decompiler::TryApplyBytecodeBackedMainChunkRestorations(
                                input_bytes,
                                normalized_relative,
                                &restored_candidate,
                                nullptr,
                                std::string_view())) {
                            restored_context.SetSource(std::move(restored_candidate));
                            std::string restored_exact_source;
                            refii::lua::audit::LuaBytecodeParityResult restored_parity;
                            if (try_promote_exact_candidate_from_context(
                                    &restored_context,
                                    &restored_exact_source,
                                    &restored_parity)) {
                                *source = std::move(restored_exact_source);
                                *parity_out = std::move(restored_parity);
                                if (note != nullptr) {
                                    const std::string prefix =
                                        local_note_prefix.empty()
                                            ? std::string("validated_exact")
                                            : std::string(local_note_prefix);
                                    *note = JoinNotes(*note,
                                                      prefix +
                                                          "_named_params_aligned");
                                }
                                return true;
                            }
                        }
                        continue;
                    }

                    return false;
                };

            const auto try_exactify_local_diff_parameter_names =
                [&](std::string* source,
                    refii::lua::audit::LuaBytecodeParityResult* parity_out,
                    std::string_view local_note_prefix) -> bool {
                    if (source == nullptr || parity_out == nullptr ||
                        parity_out->exact_bytecode ||
                        !parity_out->first_mismatch.available) {
                        return false;
                    }

                    if (std::llabs(parity_out->first_mismatch.instruction_delta) != 0 ||
                        std::llabs(parity_out->first_mismatch.constant_delta) != 0 ||
                        std::llabs(parity_out->first_mismatch.local_delta) != 0) {
                        return false;
                    }

                    struct LocalDiffEntry {
                        std::string name;
                        int start_pc = -1;
                        int end_pc = -1;
                    };
                    struct LocalDiffMismatch {
                        std::size_t local_index = 0;
                        LocalDiffEntry expected;
                        LocalDiffEntry candidate;
                    };

                    const auto parse_local_diff_entry =
                        [](std::string_view text, LocalDiffEntry* entry) -> bool {
                            if (entry == nullptr) {
                                return false;
                            }
                            const std::size_t at = text.find('@');
                            if (at == std::string_view::npos || at == 0 ||
                                at + 1 >= text.size()) {
                                return false;
                            }
                            const std::string_view name =
                                refii::lua::TrimLuaWhitespace(text.substr(0, at));
                            if (!refii::lua::IsValidLuaIdentifier(name)) {
                                return false;
                            }

                            const std::string_view range =
                                refii::lua::TrimLuaWhitespace(text.substr(at + 1));
                            const std::size_t dash = range.find('-');
                            if (dash == std::string_view::npos || dash == 0 ||
                                dash + 1 >= range.size()) {
                                return false;
                            }

                            const std::string start_text(range.substr(0, dash));
                            const std::string end_text(range.substr(dash + 1));
                            char* start_end = nullptr;
                            char* end_end = nullptr;
                            const long start_value =
                                std::strtol(start_text.c_str(), &start_end, 10);
                            const long end_value =
                                std::strtol(end_text.c_str(), &end_end, 10);
                            if (start_end == nullptr || *start_end != '\0' ||
                                end_end == nullptr || *end_end != '\0') {
                                return false;
                            }

                            entry->name = std::string(name);
                            entry->start_pc = static_cast<int>(start_value);
                            entry->end_pc = static_cast<int>(end_value);
                            return true;
                        };

                    const auto parse_local_diff_mismatch =
                        [&](std::string_view note, LocalDiffMismatch* mismatch) -> bool {
                            if (mismatch == nullptr) {
                                return false;
                            }

                            note = ExtractLocalDiffMismatchNote(note);
                            if (note.empty()) {
                                return false;
                            }

                            constexpr std::string_view kPrefix = "local_diff idx=";
                            if (!StartsWith(note, kPrefix)) {
                                return false;
                            }

                            const std::size_t index_begin = kPrefix.size();
                            std::size_t index_end = index_begin;
                            while (index_end < note.size() &&
                                   std::isdigit(static_cast<unsigned char>(note[index_end])) !=
                                       0) {
                                ++index_end;
                            }
                            if (index_end == index_begin) {
                                return false;
                            }

                            const std::string index_text(
                                note.substr(index_begin, index_end - index_begin));
                            mismatch->local_index =
                                static_cast<std::size_t>(std::strtoul(index_text.c_str(),
                                                                      nullptr,
                                                                      10));

                            const std::string_view remaining =
                                refii::lua::TrimLuaWhitespace(note.substr(index_end));
                            const std::size_t arrow = remaining.find(" -> ");
                            if (arrow == std::string_view::npos || arrow == 0 ||
                                arrow + 4 >= remaining.size()) {
                                return false;
                            }

                            const std::string_view expected_text =
                                refii::lua::TrimLuaWhitespace(remaining.substr(0, arrow));
                            const std::string_view candidate_text = refii::lua::TrimLuaWhitespace(
                                remaining.substr(arrow + 4));
                            return parse_local_diff_entry(expected_text, &mismatch->expected) &&
                                   parse_local_diff_entry(candidate_text,
                                                         &mismatch->candidate);
                        };

                    LocalDiffMismatch local_diff;
                    if (!parse_local_diff_mismatch(parity_out->first_mismatch.note,
                                                   &local_diff)) {
                        const std::string mismatch_summary =
                            refii::lua::audit::DescribeMismatchForSummary(
                                parity_out->first_mismatch);
                        if (!parse_local_diff_mismatch(mismatch_summary, &local_diff)) {
                            return false;
                        }
                    }
                    if (
                        local_diff.candidate.name == local_diff.expected.name ||
                        local_diff.candidate.start_pc != 0 ||
                        local_diff.expected.start_pc != 0 ||
                        !StartsWith(local_diff.candidate.name, "arg") ||
                        StartsWith(local_diff.expected.name, "arg") ||
                        refii::lua::IsRenderTemporaryName(local_diff.expected.name)) {
                        return false;
                    }

                    refii::lua::internal::LuaMutableTextContext source_context(*source);
                    std::vector<std::string>* lines_ptr = source_context.EnsureMutableLines();
                    if (lines_ptr == nullptr || lines_ptr->empty()) {
                        return false;
                    }
                    const bool had_trailing_newline = source_context.HadTrailingNewline();
                    const std::vector<std::string>& lines = *lines_ptr;
                    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
                        if (!refii::lua::IsRenderedFunctionStartLine(lines[line_index])) {
                            continue;
                        }
                        std::vector<std::string> function_params;
                        if (!TryExtractRenderedFunctionHeaderParameters(lines[line_index],
                                                                        &function_params) ||
                            function_params.empty()) {
                            continue;
                        }
                        if (std::find(function_params.begin(),
                                      function_params.end(),
                                      local_diff.expected.name) != function_params.end() ||
                            std::find(function_params.begin(),
                                      function_params.end(),
                                      local_diff.candidate.name) ==
                                function_params.end()) {
                            continue;
                        }

                        std::string_view stripped =
                            refii::lua::StripLuaLineComment(lines[line_index]);
                        const std::size_t function_pos = stripped.find("function");
                        const std::size_t open_paren = stripped.find('(', function_pos);
                        const std::size_t close_paren = stripped.rfind(')');
                        if (function_pos == std::string_view::npos ||
                            open_paren == std::string_view::npos ||
                            close_paren == std::string_view::npos ||
                            close_paren <= open_paren) {
                            continue;
                        }

                        for (std::string& parameter : function_params) {
                            if (parameter == local_diff.candidate.name) {
                                parameter = local_diff.expected.name;
                                break;
                            }
                        }

                        std::string rewritten_param_text;
                        for (std::size_t i = 0; i < function_params.size(); ++i) {
                            if (i != 0) {
                                rewritten_param_text += ", ";
                            }
                            rewritten_param_text += function_params[i];
                        }

                        std::vector<std::string> transformed_lines = lines;
                        transformed_lines[line_index].replace(open_paren + 1,
                                                              close_paren - open_paren - 1,
                                                              rewritten_param_text);
                        std::string exact_candidate_source;
                        refii::lua::audit::LuaBytecodeParityResult exact_parity_result;
                        if (!try_promote_exact_candidate_from_lines(
                                transformed_lines,
                                had_trailing_newline,
                                &exact_candidate_source,
                                &exact_parity_result)) {
                            continue;
                        }

                        *source = std::move(exact_candidate_source);
                        *parity_out = std::move(exact_parity_result);
                        if (note != nullptr) {
                            const std::string prefix =
                                local_note_prefix.empty()
                                    ? std::string("validated_exact")
                                    : std::string(local_note_prefix);
                            *note =
                                JoinNotes(*note, prefix + "_localdiff_parameter_renamed");
                        }
                        return true;
                    }
                    return false;
                };

            if (try_exactify_named_parameters_from_bytecode(&candidate_source,
                                                            &parity_result,
                                                            note_prefix)) {
                goto exact_parity_validated;
            }
            if (try_exactify_local_diff_parameter_names(&candidate_source,
                                                        &parity_result,
                                                        note_prefix)) {
                goto exact_parity_validated;
            }

            const bool skip_near_exact_lane_runtime_guard =
                (force_runtime_light_mode_path || runtime_heavy_family_clamp) &&
                (input_bytes.size() >= 96 * 1024 || candidate_source.size() >= 160 * 1024);
            if (skip_near_exact_lane_runtime_guard) {
                if (note != nullptr) {
                    const std::string near_exact_note_prefix =
                        note_prefix.empty()
                            ? std::string("validated_exact_near_exact")
                            : std::string(note_prefix) + "_validated_exact_near_exact";
                    *note = JoinNotes(*note,
                                      near_exact_note_prefix +
                                          "_skipped_runtime_light_mode_guard");
                }
            } else if (IsNearExactParityMismatch(parity_result)) {
                std::string near_exact_source = candidate_source;
                refii::lua::audit::LuaBytecodeParityResult near_exact_parity = parity_result;
                const std::string near_exact_note_prefix =
                    note_prefix.empty()
                        ? std::string("validated_exact_near_exact")
                        : std::string(note_prefix) + "_validated_exact_near_exact";
                const auto try_capture_near_exact_parity = [&](const std::string& source) {
                    return refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        parity_reference,
                        source,
                        normalized_relative,
                        &near_exact_parity);
                };
                const bool near_exact_tiny_instruction_or_local =
                    std::llabs(near_exact_parity.first_mismatch.instruction_delta) <= 1 &&
                    std::llabs(near_exact_parity.first_mismatch.constant_delta) == 0 &&
                    std::llabs(near_exact_parity.first_mismatch.local_delta) <= 2;
                const bool near_exact_behavior_path = HasScriptPolicyFlag(
                    normalized_relative, refii::lua::LuaScriptProfileFlag::kBehavior);
                const bool near_exact_behavior_zero_delta_instruction_diff =
                    near_exact_behavior_path &&
                    near_exact_tiny_instruction_or_local &&
                    near_exact_parity.first_mismatch.available &&
                    near_exact_parity.first_mismatch.instruction_delta == 0 &&
                    near_exact_parity.first_mismatch.constant_delta == 0 &&
                    near_exact_parity.first_mismatch.local_delta == 0;
                const bool near_exact_large_runtime_risk =
                    input_bytes.size() >= 128 * 1024 ||
                    near_exact_source.size() >= 192 * 1024;
                const bool near_exact_huge_runtime_risk =
                    input_bytes.size() >= 256 * 1024 ||
                    near_exact_source.size() >= 384 * 1024;
                const bool near_exact_behavior_bulk_runtime_guard =
                    near_exact_behavior_path &&
                    near_exact_tiny_instruction_or_local &&
                    (input_bytes.size() >= 96 * 1024 ||
                     near_exact_source.size() >= 160 * 1024);
                const bool near_exact_allow_heavy_cleanup =
                    !near_exact_large_runtime_risk &&
                    !near_exact_huge_runtime_risk &&
                    !near_exact_behavior_bulk_runtime_guard &&
                    !near_exact_behavior_zero_delta_instruction_diff;
                const bool near_exact_tiny_low_risk_budget_boost =
                    near_exact_tiny_instruction_or_local &&
                    input_bytes.size() < 96 * 1024 &&
                    near_exact_source.size() < 160 * 1024;
                const bool near_exact_allow_parity_improving_normalization =
                    near_exact_tiny_instruction_or_local &&
                    !near_exact_huge_runtime_risk &&
                    !force_runtime_light_mode_path &&
                    !runtime_heavy_family_clamp;

                bool promoted_exact = false;
                if (near_exact_tiny_instruction_or_local &&
                    near_exact_behavior_path &&
                    !near_exact_behavior_zero_delta_instruction_diff &&
                    !near_exact_huge_runtime_risk) {
                    std::string near_exact_reference_source;
                    const bool rendered_reference =
                        TryRenderNativeMainChunkCandidate(
                            input_bytes,
                            &near_exact_reference_source,
                            nullptr,
                            true,
                            near_exact_note_prefix + "_behaviour_reference",
                            normalized_relative) ||
                        TryRenderNativeMainChunkCandidate(
                            input_bytes,
                            &near_exact_reference_source,
                            nullptr,
                            false,
                            near_exact_note_prefix + "_behaviour_reference_raw",
                            normalized_relative);
                    if (rendered_reference && !near_exact_reference_source.empty()) {
                        std::string near_exact_reference_candidate = near_exact_source;
                        if (TryRepairCandidateMismatchedFunctionsFromReference(
                                input_bytes,
                                near_exact_reference_source,
                                &near_exact_reference_candidate,
                                note,
                                near_exact_note_prefix + "_behaviour_reference",
                                true) &&
                            near_exact_reference_candidate != near_exact_source &&
                            ValidateLuaSource(near_exact_reference_candidate,
                                              normalized_relative)
                                .empty() &&
                            try_capture_near_exact_parity(near_exact_reference_candidate)) {
                            near_exact_source = std::move(near_exact_reference_candidate);
                            if (near_exact_parity.exact_bytecode) {
                                candidate_source = std::move(near_exact_source);
                                parity_result = near_exact_parity;
                                promoted_exact = true;
                            }
                        }
                    }
                }
                if (promoted_exact) {
                    goto exact_parity_validated;
                }
                if (near_exact_tiny_instruction_or_local &&
                    !near_exact_behavior_zero_delta_instruction_diff &&
                    !near_exact_huge_runtime_risk) {
                    std::string near_exact_bytecode_candidate = near_exact_source;
                    if (TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                               near_exact_bytecode_candidate,
                                                               note,
                                                               near_exact_note_prefix +
                                                                   "_tiny_direct",
                                                               options,
                                                               input_path) &&
                        near_exact_bytecode_candidate != near_exact_source &&
                        ValidateLuaSource(near_exact_bytecode_candidate,
                                          normalized_relative)
                            .empty() &&
                        try_capture_near_exact_parity(near_exact_bytecode_candidate)) {
                        near_exact_source = std::move(near_exact_bytecode_candidate);
                        if (near_exact_parity.exact_bytecode) {
                            candidate_source = std::move(near_exact_source);
                            parity_result = near_exact_parity;
                            promoted_exact = true;
                        }
                    }
                }
                if (promoted_exact) {
                    goto exact_parity_validated;
                }
                const int near_exact_norm_attempt_budget =
                    near_exact_behavior_zero_delta_instruction_diff
                        ? 1
                        : near_exact_large_runtime_risk
                        ? 1
                        : (near_exact_tiny_low_risk_budget_boost ? 4 : 2);
                for (int attempt = 0;
                     attempt < near_exact_norm_attempt_budget && !promoted_exact;
                     ++attempt) {
                    if (TryApplyRenderedExactParityNormalizationCandidate(parity_reference,
                                                                          normalized_relative,
                                                                          &near_exact_source,
                                                                          note,
                                                                          near_exact_note_prefix)) {
                        candidate_source = std::move(near_exact_source);
                        try_capture_near_exact_parity(candidate_source);
                        parity_result = near_exact_parity;
                        promoted_exact = true;
                        break;
                    }
                    if (near_exact_allow_parity_improving_normalization &&
                        TryApplyRenderedParityImprovingNormalizationCandidate(
                            parity_reference,
                            normalized_relative,
                            &near_exact_source,
                            &near_exact_parity,
                            note,
                            near_exact_note_prefix)) {
                        if (near_exact_parity.exact_bytecode) {
                            candidate_source = std::move(near_exact_source);
                            parity_result = near_exact_parity;
                            promoted_exact = true;
                            break;
                        }
                        break;
                    }
                    if (!near_exact_allow_heavy_cleanup) {
                        if (note != nullptr) {
                            *note = JoinNotes(
                                *note,
                                near_exact_note_prefix +
                                    "_heavy_cleanup_skipped_runtime_guard");
                        }
                        break;
                    }
                    if (!TryApplyValidatedParityImprovingCleanupCandidate(
                            &near_exact_source,
                            input_bytes,
                            parity_reference,
                            output_path,
                            note,
                            near_exact_note_prefix)) {
                        break;
                    }
                    if (!ValidateLuaSource(near_exact_source, normalized_relative).empty()) {
                        break;
                    }
                    if (!try_capture_near_exact_parity(near_exact_source)) {
                        break;
                    }
                    if (near_exact_parity.exact_bytecode) {
                        candidate_source = std::move(near_exact_source);
                        parity_result = near_exact_parity;
                        promoted_exact = true;
                        break;
                    }
                }

                if (promoted_exact) {
                    goto exact_parity_validated;
                }

                std::string preference_reason;
                    if (ShouldPreferValidatedNonexactCandidate(near_exact_source,
                                                           near_exact_parity,
                                                           candidate_source,
                                                           parity_result,
                                                           &preference_reason)) {
                    candidate_source = std::move(near_exact_source);
                    parity_result = near_exact_parity;
                    if (note != nullptr) {
                        std::string preferred_note =
                            near_exact_note_prefix + "_preferred_nonexact";
                        if (!preference_reason.empty()) {
                            preferred_note += "_by_" + preference_reason;
                        }
                        *note = JoinNotes(*note, preferred_note);
                    }
                }
            }

            if (try_exactify_local_diff_parameter_names(&candidate_source,
                                                        &parity_result,
                                                        note_prefix)) {
                goto exact_parity_validated;
            }
            const bool final_tiny_instruction_or_local_mismatch =
                parity_result.first_mismatch.available &&
                std::llabs(parity_result.first_mismatch.instruction_delta) <= 1 &&
                std::llabs(parity_result.first_mismatch.constant_delta) == 0 &&
                std::llabs(parity_result.first_mismatch.local_delta) <= 2;
            const bool final_tiny_instruction_only_mismatch =
                parity_result.first_mismatch.available &&
                std::llabs(parity_result.first_mismatch.instruction_delta) <= 1 &&
                std::llabs(parity_result.first_mismatch.constant_delta) == 0 &&
                std::llabs(parity_result.first_mismatch.local_delta) == 0;
            const bool final_behavior_zero_delta_instruction_diff =
                final_tiny_instruction_only_mismatch &&
                HasScriptPolicyFlag(normalized_relative,
                                    refii::lua::LuaScriptProfileFlag::kBehavior) &&
                parity_result.first_mismatch.instruction_delta == 0 &&
                parity_result.first_mismatch.constant_delta == 0 &&
                parity_result.first_mismatch.local_delta == 0;
            const bool final_tiny_runtime_risk =
                (input_bytes.size() >= 192 * 1024 || candidate_source.size() >= 256 * 1024) &&
                !final_tiny_instruction_only_mismatch;
            if (final_tiny_instruction_or_local_mismatch &&
                !final_behavior_zero_delta_instruction_diff &&
                !final_tiny_runtime_risk &&
                !force_runtime_light_mode_path &&
                !runtime_heavy_family_clamp) {
                std::string final_tiny_candidate_source = candidate_source;
                if (TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                           final_tiny_candidate_source,
                                                           note,
                                                           note_prefix.empty()
                                                               ? "validated_exact_last_chance_tiny"
                                                               : std::string(note_prefix) +
                                                                     "_validated_exact_last_chance_tiny",
                                                           options,
                                                           input_path) &&
                    final_tiny_candidate_source != candidate_source &&
                    ValidateLuaSource(final_tiny_candidate_source, normalized_relative).empty()) {
                    refii::lua::audit::LuaBytecodeParityResult final_tiny_parity_result;
                    if (TryEvaluateExactParityCandidate(parity_reference,
                                                        final_tiny_candidate_source,
                                                        normalized_relative,
                                                        &final_tiny_parity_result)) {
                        if (final_tiny_parity_result.exact_bytecode) {
                            candidate_source = std::move(final_tiny_candidate_source);
                            parity_result = std::move(final_tiny_parity_result);
                            goto exact_parity_validated;
                        }
                        if (IsTinyPrepassParityImproved(final_tiny_parity_result,
                                                        parity_result)) {
                            candidate_source = std::move(final_tiny_candidate_source);
                            parity_result = std::move(final_tiny_parity_result);
                            if (note != nullptr && !note_prefix.empty()) {
                                *note = JoinNotes(
                                    *note,
                                    std::string(note_prefix) +
                                        "_validated_exact_last_chance_tiny_parity_improved");
                            }
                        }
                    }
                }
            }
        }
        if (!result.exact_parity_required && validation_error.empty()) {
            const bool allow_opportunistic_tiny_exactify =
                input_bytes.size() <= 128 * 1024 &&
                candidate_source.size() <= 192 * 1024;
            if (allow_opportunistic_tiny_exactify) {
                refii::lua::audit::LuaBytecodeParityReference opportunistic_parity_reference;
                refii::lua::audit::LuaBytecodeParityResult opportunistic_parity_result;
                if (refii::lua::audit::BuildLuaBytecodeParityReference(
                        input_bytes, &opportunistic_parity_reference) &&
                    refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        opportunistic_parity_reference,
                        candidate_source,
                        normalized_relative,
                        &opportunistic_parity_result)) {
                    if (opportunistic_parity_result.exact_bytecode) {
                        parity_result = std::move(opportunistic_parity_result);
                        goto exact_parity_validated;
                    }
                    const bool tiny_instruction_or_local_mismatch =
                        opportunistic_parity_result.first_mismatch.available &&
                        std::llabs(opportunistic_parity_result.first_mismatch.instruction_delta) <= 1 &&
                        std::llabs(opportunistic_parity_result.first_mismatch.constant_delta) == 0 &&
                        std::llabs(opportunistic_parity_result.first_mismatch.local_delta) <= 1;
                    const bool opportunistic_behavior_zero_delta_instruction_diff =
                        tiny_instruction_or_local_mismatch &&
                        HasScriptPolicyFlag(normalized_relative,
                                            refii::lua::LuaScriptProfileFlag::kBehavior) &&
                        opportunistic_parity_result.first_mismatch.instruction_delta == 0 &&
                        opportunistic_parity_result.first_mismatch.constant_delta == 0 &&
                        opportunistic_parity_result.first_mismatch.local_delta == 0;
                    if (tiny_instruction_or_local_mismatch &&
                        !opportunistic_behavior_zero_delta_instruction_diff) {
                        std::string exact_candidate_source = candidate_source;
                        refii::lua::audit::LuaBytecodeParityResult best_parity_result =
                            opportunistic_parity_result;
                        for (int attempt = 0; attempt < 2; ++attempt) {
                            const std::string before_attempt_source = exact_candidate_source;
                            if (!TryRepairNativeCandidateBytecodeParity(
                                    input_bytes,
                                    exact_candidate_source,
                                    note,
                                    note_prefix.empty()
                                        ? "validated_nonexact_tiny_exactify"
                                        : std::string(note_prefix) +
                                              "_validated_nonexact_tiny_exactify",
                                    options,
                                    input_path)) {
                                break;
                            }
                            if (exact_candidate_source == before_attempt_source ||
                                !ValidateLuaSource(exact_candidate_source, normalized_relative)
                                     .empty()) {
                                break;
                            }

                            refii::lua::audit::LuaBytecodeParityResult repaired_parity_result;
                            if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                                    opportunistic_parity_reference,
                                    exact_candidate_source,
                                    normalized_relative,
                                    &repaired_parity_result)) {
                                break;
                            }
                            if (repaired_parity_result.exact_bytecode) {
                                candidate_source = std::move(exact_candidate_source);
                                parity_result = std::move(repaired_parity_result);
                                goto exact_parity_validated;
                            }
                            if (!IsTinyPrepassParityImproved(repaired_parity_result,
                                                             best_parity_result)) {
                                break;
                            }
                            best_parity_result = repaired_parity_result;
                            candidate_source = exact_candidate_source;
                            parity_result = repaired_parity_result;
                        }
                    }
                }
            }
        }
        if (note != nullptr && !note_prefix.empty()) {
            AppendParityClassificationNote(note, note_prefix, parity_result);
            *note = JoinNotes(*note, std::string(note_prefix) + "_validated_nonexact");
        }
        if (!FinalizeValidatedLuaOutput(candidate_source,
                                        input_bytes,
                                        options,
                                        input_path,
                                        output_path,
                                        failed_output_path,
                                        note,
                                        result.exact_parity_required ? &parity_reference
                                                                     : nullptr,
                                        false)) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_validated_nonexact_rejected");
            }
            return result;
        }
        refii::lua::audit::LuaBytecodeParityResult finalized_output_parity_result;
        std::string finalized_output_source = load_finalized_output_source();
        if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                parity_reference,
                finalized_output_source,
                normalized_relative,
                &finalized_output_parity_result)) {
            parity_result = finalized_output_parity_result;
            if (finalized_output_parity_result.exact_bytecode) {
                candidate_source = std::move(finalized_output_source);
                if (note != nullptr && !note_prefix.empty()) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_validated_nonexact_promoted_post_finalize");
                }
                goto exact_parity_validated;
            }
            if (IsNearExactParityMismatch(finalized_output_parity_result)) {
                std::string post_finalize_exact_source = finalized_output_source;
                const std::string post_finalize_note_prefix =
                    note_prefix.empty()
                        ? std::string("validated_nonexact_post_finalize")
                        : std::string(note_prefix) + "_validated_nonexact_post_finalize";
                if (TryApplyRenderedExactParityNormalizationCandidate(
                        parity_reference,
                        normalized_relative,
                        &post_finalize_exact_source,
                        note,
                        post_finalize_note_prefix)) {
                    refii::lua::audit::LuaBytecodeParityResult post_finalize_parity_result;
                    if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                            parity_reference,
                            post_finalize_exact_source,
                            normalized_relative,
                            &post_finalize_parity_result) &&
                        post_finalize_parity_result.exact_bytecode) {
                        candidate_source = std::move(post_finalize_exact_source);
                        parity_result = std::move(post_finalize_parity_result);
                        if (note != nullptr && !note_prefix.empty()) {
                            *note = JoinNotes(
                                *note,
                                std::string(note_prefix) +
                                    "_validated_nonexact_exactified_post_finalize");
                        }
                        goto exact_parity_validated;
                    }
                }

                refii::lua::internal::LuaMutableTextContext post_finalize_context(
                    finalized_output_source);
                std::vector<std::string>* post_finalize_lines =
                    post_finalize_context.EnsureMutableLines();
                if (post_finalize_lines != nullptr &&
                    NormalizeRenderedLuaBranchReturnIntoFinalReturnAssignmentForParity(
                        post_finalize_lines)) {
                    post_finalize_context.MarkLinesMutated();
                    if (post_finalize_context.ValidateCached(normalized_relative).empty()) {
                        std::string improved_source =
                            std::string(post_finalize_context.EnsureSource());
                        refii::lua::audit::LuaBytecodeParityResult improved_parity_result;
                        if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                                parity_reference,
                                improved_source,
                                normalized_relative,
                                &improved_parity_result)) {
                            if (improved_parity_result.exact_bytecode) {
                                candidate_source = std::move(improved_source);
                                parity_result = std::move(improved_parity_result);
                                if (note != nullptr && !note_prefix.empty()) {
                                    *note = JoinNotes(
                                        *note,
                                        std::string(note_prefix) +
                                            "_validated_nonexact_branch_return_exactified_post_finalize");
                                }
                                goto exact_parity_validated;
                            }
                            if (IsTinyPrepassParityImproved(improved_parity_result,
                                                            finalized_output_parity_result) &&
                                FinalizeValidatedLuaOutput(
                                    improved_source,
                                    input_bytes,
                                    options,
                                    input_path,
                                    output_path,
                                    failed_output_path,
                                    note,
                                    result.exact_parity_required ? &parity_reference
                                                                 : nullptr,
                                    false)) {
                                candidate_source = std::move(improved_source);
                                parity_result = std::move(improved_parity_result);
                                if (note != nullptr && !note_prefix.empty()) {
                                    *note = JoinNotes(
                                        *note,
                                        std::string(note_prefix) +
                                            "_validated_nonexact_branch_return_improved_post_finalize");
                                }
                            }
                        }
                    }
                }
            }
        }
        result.kind = refii::lua::internal::NativeCandidateFinalizeKind::kNonexact;
        return result;
    }

exact_parity_validated:
    if (note != nullptr && !note_prefix.empty()) {
        *note = JoinNotes(*note,
                          std::string(note_prefix) +
                              (result.exact_parity_required ? "_validated_exact_parity"
                                                            : "_validated"));
    }
    if (!FinalizeValidatedLuaOutput(candidate_source,
                                    input_bytes,
                                    options,
                                    input_path,
                                    output_path,
                                    failed_output_path,
                                    note,
                                    result.exact_parity_required ? &parity_reference : nullptr,
                                    result.exact_parity_required)) {
        return result;
    }
    result.kind = refii::lua::internal::NativeCandidateFinalizeKind::kExact;
    return result;
}

#include "decompiler/rendered/pipelines/native_candidate_classification_helpers.inc"

#include "decompiler/rendered/pipelines/ordered_syntax_state_loop_repairs.inc"

#include "decompiler/rendered/pipelines/ordered_syntax_generator_repairs.inc"

#include "decompiler/rendered/pipelines/ordered_syntax_repair_passes.inc"

#include "decompiler/rendered/pipelines/raw_invalid_structural_normalization.inc"

#include "decompiler/rendered/pipelines/raw_invalid_structural_prepass.inc"

#include "decompiler/rendered/pipelines/native_candidate_repair_pipeline.inc"

bool ValidateAndRepairDecompiledOutput(const std::filesystem::path& relative_input,
                                       std::string_view source_name,
                                       const std::vector<char>& input_bytes,
                                       const refii::lua::LuaWorkspaceDecompileOptions* options,
                                       const std::filesystem::path* input_path,
                                       const std::filesystem::path& output_path,
                                       const std::filesystem::path& failed_output_path,
                                       std::string* note) {
    std::string decompiled_source;
    if (!ReadTextFile(output_path, decompiled_source)) {
        if (note) {
            *note = JoinNotes(*note, "read_decompiled_output_failed");
        }
        return false;
    }
    StripLegacyDecompilerBanner(&decompiled_source, note);
    const std::string normalized_relative = NormalizePathString(relative_input.generic_string());
    refii::lua::internal::LuaMutableTextContext decompiled_text_context(decompiled_source);
    const auto validate_decompiled_source = [&]() {
        decompiled_text_context.SetSourceView(decompiled_source);
        return decompiled_text_context.ValidateCached(normalized_relative);
    };
    const refii::lua::LuaScriptPolicy repair_policy =
        refii::lua::BuildLuaScriptPolicy(normalized_relative);
    constexpr std::size_t kQuestHotspotRepairSkipThresholdBytes = 100000;
    const bool is_quest_hotspot =
        input_bytes.size() >= kQuestHotspotRepairSkipThresholdBytes &&
        repair_policy.runtime_fallback_quest_hotspot;
    const bool force_runtime_light_mode_path =
        IsRuntimeLightModePath(repair_policy, input_bytes.size());
    const bool runtime_heavy_family_clamp =
        ShouldClampRuntimeHeavyRepairFamilies(repair_policy, input_bytes.size());
    const bool allow_exhaustive_rescue =
        ShouldUseExhaustiveRescueForCurrentFile() && !is_quest_hotspot &&
        !force_runtime_light_mode_path;
    if (force_runtime_light_mode_path && note != nullptr) {
        *note = JoinNotes(*note, "validated_output_runtime_light_mode");
    }
    if (ShouldPreferNativeFirstForCanonicalDebugBank(relative_input, input_bytes)) {
        const std::string legacy_decompiled_source = decompiled_source;
        std::string native_first_note;
        if (TryReplaceWithValidatedNativeMainChunk(input_bytes,
                                                   decompiled_source,
                                                   &native_first_note,
                                                   options,
                                                   input_path,
                                                   "simplified")) {
            const std::string native_validation_error =
                validate_decompiled_source();
            const LuaOutputQualitySummary native_quality =
                SummarizeLuaOutputQuality(decompiled_source);
            if (native_validation_error.empty() &&
                !HasBlockingLuaOutputQualityIssues(native_quality) &&
                !ContainsObviouslyInvalidNilUsage(decompiled_source)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note, native_first_note);
                    *note = JoinNotes(*note,
                                      "native_first_debug_bank_baseline_validated");
                }
                TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                       decompiled_source,
                                                       note,
                                                       "native_first_finalized_output",
                                                       options,
                                                       input_path);
                return FinalizeValidatedLuaOutput(decompiled_source,
                                                  input_bytes,
                                                  options,
                                                  input_path,
                                                  output_path,
                                                  failed_output_path,
                                                  note);
            }

            if (note != nullptr) {
                if (!native_first_note.empty()) {
                    *note = JoinNotes(*note, native_first_note);
                }
                *note = JoinNotes(*note,
                                  "native_first_debug_bank_baseline_rejected");
            }
            decompiled_source = legacy_decompiled_source;
        }
    }
    std::string validation_error;
    std::unordered_set<std::string> heavy_repair_family_states;
    const auto allow_heavy_repair_family = [&](std::string_view family) {
        if (runtime_heavy_family_clamp && IsRuntimeHeavyRepairFamily(family)) {
            AppendNoProgressNote(note, std::string(family) + "_runtime_clamped");
            return false;
        }
        return ShouldAttemptRepairFamilyForState(
            &heavy_repair_family_states, family, decompiled_source, validation_error, note);
    };
    const auto persist_output_snapshot = [&]() {
        WriteNormalizedLuaTextFile(output_path, decompiled_source, note);
    };
    const auto persist_and_revalidate = [&]() {
        persist_output_snapshot();
        validation_error = validate_decompiled_source();
    };
    const auto try_apply_and_revalidate = [&](auto&& apply_once) {
        if (validation_error.empty()) {
            return false;
        }
        if (!apply_once()) {
            return false;
        }
        persist_and_revalidate();
        return true;
    };
    if (!is_quest_hotspot &&
        TryRepairNativeCandidateBytecodeParity(input_bytes,
                                               decompiled_source,
                                               note,
                                               "validated_output",
                                               options,
                                               input_path)) {
        persist_output_snapshot();
    }
    validation_error = validate_decompiled_source();
    if (!validation_error.empty()) {
        TryApplyTargetedRawInvalidStructuralPreRepair(
            decompiled_source, &validation_error, normalized_relative, note);
    }
    try_apply_and_revalidate(
        [&]() { return TryStripRedundantEndBeforeTopLevelBoundary(decompiled_source, note); });
    try_apply_and_revalidate(
        [&]() { return TryRewriteRepeatIfYieldElseContinuations(decompiled_source, note); });
    try_apply_and_revalidate([&]() {
        return TryStripDanglingDoBeforeBoundary(decompiled_source, validation_error, note);
    });
    const auto try_boundary_cleanup_cycle = [&]() {
        bool changed_any = false;
        const int boundary_cleanup_pass_cap = GetCurrentRepairAttemptCap(2, 1);
        RunBoundedNormalizationPassLoop(boundary_cleanup_pass_cap, [&](int /*pass*/) {
            if (validation_error.empty()) {
                return false;
            }
            bool pass_changed = false;
            if (try_apply_and_revalidate(
                    [&]() { return TryInsertEndBeforeElse(decompiled_source, validation_error, note); })) {
                pass_changed = true;
            }
            if (try_apply_and_revalidate(
                    [&]() { return TryNormalizeQuestOwnerContinuationCleanup(decompiled_source, note); })) {
                pass_changed = true;
            }
            changed_any |= pass_changed;
            return pass_changed;
        });
        return changed_any;
    };
    if (!validation_error.empty()) {
        try_boundary_cleanup_cycle();
    }
    const bool initial_has_blocking_quality =
        HasBlockingLuaOutputQualityIssues(SummarizeLuaOutputQuality(decompiled_source));
    if (!is_quest_hotspot &&
        (allow_exhaustive_rescue || !validation_error.empty() ||
         initial_has_blocking_quality) &&
        TryPreCleanCandidateLuaSource(decompiled_source, note)) {
        if (note != nullptr) {
            *note = JoinNotes(*note, "validated_output_precleaned");
        }
        persist_and_revalidate();
    }
    if (allow_exhaustive_rescue &&
        allow_heavy_repair_family("shared_rendered_cleanup") &&
        (!validation_error.empty() ||
         HasBlockingLuaOutputQualityIssues(SummarizeLuaOutputQuality(decompiled_source))) &&
        TryApplySharedRenderedOutputCleanup(decompiled_source,
                                            validation_error,
                                            normalized_relative,
                                            note)) {
    }
    const LuaOutputQualitySummary baseline_quality =
        SummarizeLuaOutputQuality(decompiled_source);
    const auto finalize_invalid_output = [&]() -> bool {
        if (note) {
            *note = JoinNotes(*note, "syntax_invalid");
            AppendSyntaxErrorNotes(note,
                                   "syntax_error",
                                   "syntax_error_line",
                                   validation_error);
        }
        if (!failed_output_path.empty()) {
            std::filesystem::create_directories(failed_output_path.parent_path());
            if (WriteNormalizedLuaTextFile(failed_output_path, std::move(decompiled_source), note)) {
                if (note) {
                    *note = JoinNotes(
                        *note, "failed_output_path=" +
                                   std::filesystem::path("_failed")
                                       .append(relative_input.generic_string())
                                       .generic_string());
                }
            } else if (note) {
                *note = JoinNotes(*note, "failed_output_write_failed");
            }
        }
        return false;
    };
    const auto finalize_current_output = [&]() -> bool {
        if (!is_quest_hotspot) {
            TryRepairNativeCandidateBytecodeParity(input_bytes,
                                                   decompiled_source,
                                                   note,
                                                   "finalized_output",
                                                   options,
                                                   input_path);
        }
        return FinalizeValidatedLuaOutput(decompiled_source,
                                          input_bytes,
                                          options,
                                          input_path,
                                          output_path,
                                          failed_output_path,
                                          note);
    };
    const auto normalize_quest_owner_cleanup = [&]() -> bool {
        if (!TryNormalizeQuestOwnerContinuationCleanup(decompiled_source, note)) {
            return false;
        }
        validation_error = validate_decompiled_source();
        return true;
    };
    const auto has_blocking_quality_issues = [&](const LuaOutputQualitySummary& quality) {
        return HasBlockingLuaOutputQualityIssues(quality) ||
               ContainsObviouslyInvalidNilUsage(decompiled_source);
    };
    const auto refresh_validation_with_owner_cleanup = [&]() {
        validation_error = validate_decompiled_source();
        normalize_quest_owner_cleanup();
    };
    const auto validate_and_try_finalize = [&]() -> bool {
        validation_error = validate_decompiled_source();
        if (!validation_error.empty()) {
            return false;
        }
        const LuaOutputQualitySummary quality = SummarizeLuaOutputQuality(decompiled_source);
        if (has_blocking_quality_issues(quality)) {
            return false;
        }
        return finalize_current_output();
    };
    const auto normalize_validate_and_try_finalize = [&]() -> bool {
        refresh_validation_with_owner_cleanup();
        if (!validation_error.empty()) {
            return false;
        }
        const LuaOutputQualitySummary quality = SummarizeLuaOutputQuality(decompiled_source);
        if (has_blocking_quality_issues(quality)) {
            return false;
        }
        return finalize_current_output();
    };
    const auto try_replace_with_validated_native_main_chunk = [&]() -> bool {
        if (!allow_heavy_repair_family("replace_validated_native_main_chunk")) {
            return false;
        }
        return TryReplaceWithValidatedNativeMainChunk(
            input_bytes, decompiled_source, note, options, input_path);
    };
    const auto try_replace_decompiler_error_functions = [&](bool include_full_retry) -> bool {
        if (!allow_heavy_repair_family("replace_decompiler_error_functions")) {
            return false;
        }
        return TryReplaceDecompilerErrorFunctions(
            input_bytes, decompiled_source, note, include_full_retry, options, input_path);
    };
    const auto try_fast_large_chunk_syntax_repair =
        [&](std::string* current_validation_error) -> bool {
        if (current_validation_error == nullptr || current_validation_error->empty()) {
            return false;
        }

        bool tried_insert_before_reported_line = false;
        const OrderedSyntaxRepairPassOptions fast_large_chunk_repair_options = {
            .include_top_level_leakage_cleanup = false,
            .include_member_reference_repairs = false,
            .include_for_generator_repairs = false,
            .include_generator_comment_repairs = false,
            .include_while_true_to_repeat = false,
            .include_insert_end_before_return = false,
            .include_insert_end_before_until = false,
            .include_insert_until_before_trailing_end = false,
            .include_insert_ends_before_top_level_boundaries = false,
        };
        const SyntaxRepairAttemptResult repair_result = RunOrderedSyntaxRepairPassAttempts(
            decompiled_source,
            *current_validation_error,
            normalized_relative,
            note,
            &tried_insert_before_reported_line,
            fast_large_chunk_repair_options,
            8,
            false);
        const bool changed_any = repair_result != SyntaxRepairAttemptResult::kNoChange;
        if (repair_result == SyntaxRepairAttemptResult::kValidated) {
            current_validation_error->clear();
        }

        if (changed_any && note != nullptr) {
            if (current_validation_error->empty()) {
                *note = JoinNotes(*note, "large_chunk_fast_syntax_repair_validated");
            } else {
                AppendSyntaxErrorNotes(note,
                                       "large_chunk_fast_syntax_repair_progress",
                                       "large_chunk_fast_syntax_repair_line",
                                       *current_validation_error);
            }
        }
        if (changed_any && normalize_quest_owner_cleanup()) {
            *current_validation_error = validation_error;
        }
        return changed_any;
    };
    const auto try_settled_boundary_syntax_repair = [&]() -> bool {
        if (validation_error.empty() ||
            !TryRemoveEndsBeforeTopLevelBoundaries(
                decompiled_source, validation_error, note)) {
            return false;
        }

        validation_error = validate_decompiled_source();
        if (validation_error.empty()) {
            return finalize_current_output();
        }

        const SyntaxRepairAttemptResult after_boundary_trim_anchored_result =
            TryRunAnchoredValidatedSyntaxRepairs(
                decompiled_source, validation_error, normalized_relative, note);
        if (after_boundary_trim_anchored_result ==
            SyntaxRepairAttemptResult::kValidated) {
            return finalize_current_output();
        }
        validation_error = validate_decompiled_source();
        return false;
    };
    const bool baseline_has_blocking_quality =
        HasBlockingLuaOutputQualityIssues(baseline_quality) ||
        ContainsObviouslyInvalidNilUsage(decompiled_source);
    if (validation_error.empty() &&
        !HasBlockingLuaOutputQualityIssues(baseline_quality) &&
        !ContainsObviouslyInvalidNilUsage(decompiled_source)) {
        return finalize_current_output();
    }
    if (is_quest_hotspot && !validation_error.empty()) {
        if (note != nullptr) {
            *note = JoinNotes(*note, "quest_hotspot_fast_fail");
        }
        return finalize_invalid_output();
    }

    const bool large_chunk = input_bytes.size() > 150000;
    const bool tractable_large_chunk_native_repair =
        large_chunk && ShouldAllowInProcessNativeMainChunkFallback(input_bytes);
    const bool allow_large_chunk_syntax_repair =
        large_chunk && IsLargeChunkSyntaxRepairCandidate(validation_error);
    const auto try_large_chunk_failed_rescue = [&]() -> bool {
        if (runtime_heavy_family_clamp) {
            AppendNoProgressNote(note, "large_chunk_failed_rescue_runtime_clamped");
            return false;
        }
        bool changed = false;

        if (!validation_error.empty() &&
            allow_heavy_repair_family("repair_reported_functions_iterative") &&
            TryRepairReportedFunctionsFromBytecodeIteratively(
                input_bytes,
                decompiled_source,
                &validation_error,
                note,
                "large_chunk_fast_fail_reported_function_repaired")) {
            changed = true;
            normalize_quest_owner_cleanup();
            if (validation_error.empty()) {
                return true;
            }
        }

        if ((!validation_error.empty() || baseline_has_blocking_quality) &&
            try_replace_decompiler_error_functions(false)) {
            changed = true;
            if (normalize_validate_and_try_finalize()) {
                return true;
            }
        }

        if ((!validation_error.empty() || baseline_has_blocking_quality) &&
            tractable_large_chunk_native_repair &&
            try_replace_with_validated_native_main_chunk()) {
            changed = true;
            if (normalize_validate_and_try_finalize()) {
                return true;
            }
        }

        return changed;
    };
    if (large_chunk && !validation_error.empty()) {
        try_fast_large_chunk_syntax_repair(&validation_error);
        if (validation_error.empty()) {
            return finalize_current_output();
        }
        if (input_bytes.size() > kVeryLargeChunkFastFailThresholdBytes) {
            const SyntaxRepairAttemptResult fast_fail_result =
                TryRunAnchoredValidatedSyntaxRepairs(
                    decompiled_source, validation_error, normalized_relative, note);
            if (fast_fail_result == SyntaxRepairAttemptResult::kValidated) {
                return finalize_current_output();
            }
            if (fast_fail_result == SyntaxRepairAttemptResult::kChanged) {
                normalize_quest_owner_cleanup();
            }
            const bool rescued_after_fast_syntax_prepass =
                allow_exhaustive_rescue && try_large_chunk_failed_rescue();
            if (validation_error.empty()) {
                return finalize_current_output();
            }
            if (rescued_after_fast_syntax_prepass) {
                if (note) {
                    *note = JoinNotes(*note, "large_chunk_fast_fail_rescue_progress");
                }
            } else {
                if (try_settled_boundary_syntax_repair()) {
                    return true;
                }
                if (note) {
                    *note = JoinNotes(*note, "large_chunk_fast_failed_after_syntax_prepass");
                }
                return finalize_invalid_output();
            }
        }
    }
    if (tractable_large_chunk_native_repair && baseline_has_blocking_quality &&
        try_replace_decompiler_error_functions(false)) {
        if (normalize_validate_and_try_finalize()) {
            return true;
        }
    }

    if (large_chunk && (baseline_has_blocking_quality || !validation_error.empty())) {
        if (try_replace_with_validated_native_main_chunk()) {
            if (normalize_validate_and_try_finalize()) {
                return true;
            }
        }
    }
    if (large_chunk && !allow_large_chunk_syntax_repair) {
        if (note) {
            *note = JoinNotes(*note, "validation_fast_failed_large_chunk");
        }
        return finalize_invalid_output();
    }

    if (baseline_has_blocking_quality && try_replace_with_validated_native_main_chunk()) {
        refresh_validation_with_owner_cleanup();
        if (validation_error.empty()) {
            const LuaOutputQualitySummary native_quality =
                SummarizeLuaOutputQuality(decompiled_source);
            if (!has_blocking_quality_issues(native_quality)) {
                return finalize_current_output();
            }
        }
    }
    bool retried_native_replacements = false;
    if (baseline_has_blocking_quality) {
        retried_native_replacements = try_replace_decompiler_error_functions(false);
    }

    refresh_validation_with_owner_cleanup();
    LuaOutputQualitySummary current_quality =
        SummarizeLuaOutputQuality(decompiled_source);
    if (validation_error.empty() &&
        TryRepairValidatedTopLevelFunctionBoundaryBleed(decompiled_source, note)) {
        validation_error = validate_decompiled_source();
        current_quality = SummarizeLuaOutputQuality(decompiled_source);
    }
    if (validation_error.empty()) {
        if (has_blocking_quality_issues(current_quality) &&
            try_replace_with_validated_native_main_chunk()) {
            if (normalize_validate_and_try_finalize()) {
                return true;
            }
        }
        return finalize_current_output();
    }

    if (try_replace_with_validated_native_main_chunk()) {
        if (validate_and_try_finalize()) {
            return true;
        }
    }
    const bool skip_expensive_syntax_repair =
        large_chunk && !allow_large_chunk_syntax_repair;
    if (skip_expensive_syntax_repair) {
        if (note) {
            *note = JoinNotes(*note, "syntax_repair_skipped_large_chunk");
        }
    } else if (allow_large_chunk_syntax_repair && note) {
        *note = JoinNotes(*note, "syntax_repair_allowed_large_chunk");
    }

    bool tried_insert_before_reported_line = false;
    std::unordered_set<std::string> seen_states;
    std::unordered_set<std::string> seen_quality_states;
    std::unordered_map<std::string, int> seen_family_states;
    const OrderedSyntaxRepairPassOptions main_syntax_repair_options = {
        .include_top_level_leakage_cleanup = true,
        .include_member_reference_repairs = true,
        .include_for_generator_repairs = true,
        .include_generator_comment_repairs = true,
        .include_while_true_to_repeat = true,
        .include_insert_end_before_return = true,
        .include_insert_end_before_until = true,
        .include_insert_until_before_trailing_end = true,
        .include_insert_ends_before_top_level_boundaries = true,
    };
    const int max_syntax_repair_attempts = is_quest_hotspot
                                               ? GetCurrentRepairAttemptCap(4, 2)
                                               : GetRuntimeAwareSyntaxRepairAttemptCap(
                                                     is_quest_hotspot,
                                                     large_chunk,
                                                     runtime_heavy_family_clamp,
                                                     force_runtime_light_mode_path);
    std::string previous_top_level_replacement_state;
    int repeated_top_level_replacement_rounds = 0;
    bool top_level_replacement_cutoff = false;
    const auto note_top_level_replacement_progress = [&](std::string_view stage_label) {
        if (top_level_replacement_cutoff) {
            return;
        }
        const std::string replacement_state =
            BuildTopLevelReplacementState(decompiled_source, validation_error);
        if (replacement_state == previous_top_level_replacement_state) {
            ++repeated_top_level_replacement_rounds;
        } else {
            repeated_top_level_replacement_rounds = 0;
            previous_top_level_replacement_state = replacement_state;
        }
        if (repeated_top_level_replacement_rounds >= 2) {
            top_level_replacement_cutoff = true;
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "top_level_replacement_no_progress_cutoff@" +
                                      std::string(stage_label));
            }
        }
    };
    const auto try_replace_top_level_error_functions = [&](std::string_view stage_label) {
        if (validation_error.empty() || top_level_replacement_cutoff) {
            return;
        }
        const std::string before_native_retry = decompiled_source;
        const bool replaced = try_replace_decompiler_error_functions(false);
        if (replaced && decompiled_source != before_native_retry) {
            retried_native_replacements = true;
            validation_error = validate_decompiled_source();
        }
        note_top_level_replacement_progress(stage_label);
    };
    for (int attempt = 0;
        attempt < max_syntax_repair_attempts && !validation_error.empty() &&
        !skip_expensive_syntax_repair;
         ++attempt) {
        if (ShouldAbortCurrentDecompileRepair(note, "validated_output_syntax_repair")) {
            break;
        }
        const std::string before_state =
            BuildRepairProgressStateKey(decompiled_source, validation_error);
        const std::string before_quality_state =
            BuildRepairQualityStateKey(decompiled_source, validation_error);
        const std::string before_family_state =
            BuildRepairFamilyStateKey(decompiled_source, validation_error);
        if (!seen_states.insert(before_state).second) {
            if (note) {
                *note = JoinNotes(*note, "syntax_repair_cycle_detected");
            }
            break;
        }
        if (!seen_quality_states.insert(before_quality_state).second) {
            AppendNoProgressNote(note, "syntax_repair_no_progress");
            break;
        }
        if (++seen_family_states[before_family_state] > GetCurrentRepairFamilyRepeatBudget()) {
            AppendNoProgressNote(note, "syntax_repair_no_progress");
            break;
        }

        const SyntaxRepairAttemptResult repair_result = TryRunOrderedSyntaxRepairPass(
            decompiled_source,
            validation_error,
            normalized_relative,
            note,
            &tried_insert_before_reported_line,
            main_syntax_repair_options);
        if (repair_result != SyntaxRepairAttemptResult::kNoChange) {
            if (repair_result == SyntaxRepairAttemptResult::kValidated) {
                return finalize_current_output();
            }
            validation_error = validate_decompiled_source();
            const std::string after_quality_state =
                BuildRepairQualityStateKey(decompiled_source, validation_error);
            if (after_quality_state == before_quality_state) {
                AppendNoProgressNote(note, "syntax_repair_no_progress");
                break;
            }
            try_replace_top_level_error_functions("syntax_loop");
            if (validation_error.empty()) {
                return finalize_current_output();
            }
            continue;
        }

        if (!TryCommentReportedSyntaxLine(decompiled_source, validation_error, note)) {
            break;
        }

        validation_error = validate_decompiled_source();
        const std::string after_comment_quality_state =
            BuildRepairQualityStateKey(decompiled_source, validation_error);
        if (after_comment_quality_state == before_quality_state) {
            AppendNoProgressNote(note, "syntax_repair_no_progress");
            break;
        }
        try_replace_top_level_error_functions("syntax_comment");
        if (validation_error.empty()) {
            return finalize_current_output();
        }
    }

    if (!skip_expensive_syntax_repair && allow_exhaustive_rescue &&
        !top_level_replacement_cutoff) {
        const std::string before_native_retry = decompiled_source;
        if (try_replace_decompiler_error_functions(true) &&
            decompiled_source != before_native_retry) {
            retried_native_replacements = true;
            validation_error = validate_decompiled_source();
            if (validation_error.empty()) {
                return finalize_current_output();
            }
            decompiled_source = before_native_retry;
            validation_error = validate_decompiled_source();
            if (note) {
                *note = JoinNotes(*note, "native_full_retry_reverted_after_invalid_result");
            }
        }
        note_top_level_replacement_progress("full_retry");
    }

    const SyntaxRepairAttemptResult final_anchored_result =
        TryRunAnchoredValidatedSyntaxRepairs(
            decompiled_source, validation_error, normalized_relative, note);
    if (final_anchored_result == SyntaxRepairAttemptResult::kValidated) {
        return finalize_current_output();
    }
    if (try_settled_boundary_syntax_repair()) {
        return true;
    }

    return finalize_invalid_output();
}

}  // namespace

#include "refii_lua_decompiler_internal_bridge.inc"

namespace refii::lua {

std::filesystem::path GetDefaultLuaDecompiledOutputRoot(const std::filesystem::path& workspace_root) {
    return IsSourceWorkspaceRoot(workspace_root) ? workspace_root.parent_path() : workspace_root;
}

std::filesystem::path GetDefaultLuaDecompileInputRoot(const std::filesystem::path& workspace_root) {
    if (IsSourceWorkspaceRoot(workspace_root)) {
        return workspace_root;
    }

    const std::filesystem::path source_root = workspace_root / ".source";
    std::error_code error;
    if (std::filesystem::exists(source_root, error) && std::filesystem::is_directory(source_root, error)) {
        return source_root;
    }

    return workspace_root;
}

}  // namespace refii::lua
