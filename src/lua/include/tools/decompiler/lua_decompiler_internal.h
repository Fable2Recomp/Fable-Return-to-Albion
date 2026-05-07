#pragma once

#include "lua_script_paths.h"
#include "tools/decompiler/lua51_analysis.h"
#include "tools/decompiler/decompiler_file_io.h"
#include "tools/bytecode/lua_bytecode_oracles.h"
#include "tools/decompiler/repair/lua_decompile_repair_context.h"
#include "tools/decompiler/rendered/output_placeholder_helpers.h"
#include "tools/native/native_lua_decompiler.h"
#include "tools/native/native_lua_chunk.h"

#include <chrono>
#include <cctype>
#include <filesystem>
#include <functional>
#include <limits>
#include <optional>
#include <string>
#include <string_view>
#include <type_traits>
#include <unordered_map>
#include <vector>

namespace refii::lua::internal {

struct ResolvedInput {
    std::filesystem::path path;
    std::string note;
};

std::string NormalizePathString(std::string value);
bool StartsWith(std::string_view value, std::string_view prefix);
bool EndsWith(std::string_view value, std::string_view suffix);
int GetInputPriority(const std::filesystem::path& path);
bool IsLuaBytecode(const std::vector<char>& bytes);
std::string DecodeSourceName(const std::vector<char>& bytes);
std::string DescribeBytecodeChunk(const std::vector<char>& bytes);
std::string_view StripLineComment(std::string_view line);
std::string_view TrimWhitespace(std::string_view value);
std::vector<std::string> SplitLines(std::string_view text, bool* had_trailing_newline);
std::string JoinLines(const std::vector<std::string>& lines, bool had_trailing_newline);
ResolvedInput ResolveBestInputPath(const std::filesystem::path& workspace,
                                   const std::filesystem::path& relative_path);
bool ReadFile(const std::filesystem::path& path, std::vector<char>& bytes);
int RunDecompiler(const LuaWorkspaceDecompileOptions& options,
                  const std::filesystem::path& input_path,
                  const std::filesystem::path& output_path,
                  const std::vector<std::string>& extra_args = {},
                  bool force_external_tool = false,
                  unsigned int timeout_seconds = std::numeric_limits<unsigned int>::max());
std::string BuildIgnoredOutputPrefix(const std::filesystem::path& workspace_root,
                                     const std::filesystem::path& output_root);
std::string DescribeRunResult(std::string_view label, int result);
std::string JoinNotes(std::string lhs, std::string rhs);
std::string GetLuaLineIndent(std::string_view line);
bool ContainsWord(std::string_view text, std::string_view word);
bool LooksLikeDecompilerTemporaryName(std::string_view name);
bool ContainsIdentifierReference(std::string_view line, std::string_view identifier);
std::string_view ExtractLeadingIdentifier(std::string_view trimmed);
std::string_view ExtractLocalDeclarationName(std::string_view trimmed);

enum class LuaTokenScanMode {
    kRaw,
    kCommentAware,
};

struct LuaTokenScanOptions {
    LuaTokenScanMode mode = LuaTokenScanMode::kRaw;
    bool parse_quoted_literals = false;
    bool use_stable_copy = false;
};

template <typename Callback>
void ForEachLuaIdentifierToken(std::string_view line,
                               const LuaTokenScanOptions& options,
                               Callback&& callback) {
    const std::string stable_line = options.use_stable_copy ? std::string(line) : std::string();
    const std::string_view scan = options.use_stable_copy ? std::string_view(stable_line) : line;

    const auto is_identifier_char = [](char ch) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        return std::isalnum(uch) != 0 || ch == '_';
    };

    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = 0; i < scan.size();) {
        if (options.mode == LuaTokenScanMode::kCommentAware && !in_single_quote &&
            !in_double_quote && i + 1 < scan.size() && scan[i] == '-' &&
            scan[i + 1] == '-') {
            break;
        }

        if (options.parse_quoted_literals && in_single_quote) {
            if (scan[i] == '\\' && i + 1 < scan.size()) {
                i += 2;
            } else {
                in_single_quote = scan[i] != '\'';
                ++i;
            }
            continue;
        }

        if (options.parse_quoted_literals && in_double_quote) {
            if (scan[i] == '\\' && i + 1 < scan.size()) {
                i += 2;
            } else {
                in_double_quote = scan[i] != '"';
                ++i;
            }
            continue;
        }

        if (options.parse_quoted_literals && scan[i] == '\'') {
            in_single_quote = true;
            ++i;
            continue;
        }
        if (options.parse_quoted_literals && scan[i] == '"') {
            in_double_quote = true;
            ++i;
            continue;
        }

        const unsigned char ch = static_cast<unsigned char>(scan[i]);
        if ((std::isalpha(ch) == 0) && scan[i] != '_') {
            ++i;
            continue;
        }

        const std::size_t begin = i;
        ++i;
        while (i < scan.size() && is_identifier_char(scan[i])) {
            ++i;
        }

        const std::string_view token = scan.substr(begin, i - begin);
        if constexpr (std::is_invocable_v<Callback, std::string_view, std::size_t, std::size_t>) {
            callback(token, begin, i);
        } else {
            callback(token);
        }
    }
}

bool IsLuaYieldWaitLine(std::string_view line);
bool IsSuppressedControlMarker(std::string_view line);
bool IsSuppressedElseControlMarker(std::string_view line);
std::size_t FindNextNonEmptyLuaLine(const std::vector<std::string>& lines,
                                    std::size_t start,
                                    bool include_suppressed_else = false);
std::size_t FindFirstNonEmptyLuaLineInRange(const std::vector<std::string>& lines,
                                            std::size_t begin,
                                            std::size_t end);
std::size_t FindLastNonEmptyLuaLineInRange(const std::vector<std::string>& lines,
                                           std::size_t begin,
                                           std::size_t end);
std::size_t FindPreviousNonEmptyLuaLine(const std::vector<std::string>& lines,
                                        std::size_t start);
std::size_t FindNextSignificantLuaLine(const std::vector<std::string>& lines,
                                       std::size_t start);
bool ParseLuaIfCondition(std::string_view line, std::string* condition);
bool ParseLuaWhileCondition(std::string_view line, std::string* condition);
int ComputeLuaBlockDelta(std::string_view line);
std::size_t FindBalancedLuaBlockEnd(const std::vector<std::string>& lines, std::size_t start);
void AppendReindentedLuaBlock(const std::vector<std::string>& lines,
                              std::size_t begin,
                              std::size_t end,
                              std::string_view old_prefix,
                              std::string_view new_prefix,
                              std::vector<std::string>* output,
                              bool skip_suppressed_markers = false);
bool CopyNormalizedLuaTextFile(const std::filesystem::path& from,
                               const std::filesystem::path& to,
                               std::string* note);
void ResetTransientDecompileCaches();
using LuaQuestSimpleRepairFunction = bool (*)(std::string&, std::string*);
using LuaQuestValidatedRepairFunction = bool (*)(std::string&,
                                                 std::string_view,
                                                 std::string*);
struct SharedQuestWaitRepairStageOptions {
    bool include_reported_repeat_repair = false;
    bool include_extended_wait_repairs = false;
};
struct LuaQuestRepairStageOps {
    LuaQuestSimpleRepairFunction try_split_duplicated_else_wait_ladders = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_repeat_if_yield_else_continuations = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_repeat_state_ladder_loops = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_repeat_leading_state_loops = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_repeat_state_machine_loops = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_repeat_monitor_loops_without_until = nullptr;
    LuaQuestSimpleRepairFunction try_break_empty_then_yield_else_continuations = nullptr;
    LuaQuestSimpleRepairFunction try_break_repeated_wait_else_continuations = nullptr;
    LuaQuestValidatedRepairFunction try_rewrite_reported_dangling_repeat_loop = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_dangling_repeat_loops = nullptr;
    LuaQuestSimpleRepairFunction try_lift_state_chain_after_do_return_end = nullptr;
    LuaQuestSimpleRepairFunction try_lift_state_continuation_after_wait_loop = nullptr;
    LuaQuestValidatedRepairFunction try_rewrite_do_return_end_branches = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_immediate_yield_else_wait_loops = nullptr;
    LuaQuestSimpleRepairFunction try_strip_malformed_repeat_wait_wrappers = nullptr;
    LuaQuestSimpleRepairFunction try_lift_malformed_yield_wait_continuations = nullptr;
    LuaQuestSimpleRepairFunction try_simplify_rendered_wait_loop_patterns = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_balanced_outer_true_guarded_wait_blocks = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_nested_repeated_wait_continuations = nullptr;
    LuaQuestSimpleRepairFunction try_break_suppressed_else_wait_continuations = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_suppressed_else_sentinel_while_loops = nullptr;
    LuaQuestSimpleRepairFunction try_lift_true_wrapped_wait_loop_continuations = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_yield_trapped_wait_continuations = nullptr;
    LuaQuestSimpleRepairFunction try_flatten_degenerate_true_wait_wrappers = nullptr;
    LuaQuestValidatedRepairFunction try_strip_dangling_do_before_boundary = nullptr;
    LuaQuestValidatedRepairFunction try_strip_dangling_do_inside_true_loop = nullptr;
    LuaQuestSimpleRepairFunction try_lift_trailing_yield_across_suppressed_close_run = nullptr;
    LuaQuestSimpleRepairFunction try_collapse_suppressed_else_yield_tail_state_loops = nullptr;
    LuaQuestSimpleRepairFunction try_split_repeated_wait_collection_loops = nullptr;
    LuaQuestSimpleRepairFunction try_normalize_quest_owner_continuation_cleanup = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_immediate_anonymous_function_invocations = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_split_method_reference_self_calls = nullptr;
    LuaQuestSimpleRepairFunction
        try_rewrite_bare_method_reference_constructor_assignments = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_bare_method_reference_assignments = nullptr;
    LuaQuestSimpleRepairFunction try_rewrite_invalid_method_field_assignments = nullptr;
    LuaQuestSimpleRepairFunction try_apply_shared_unhandled_if_comment_cleanup = nullptr;
    LuaQuestSimpleRepairFunction
        try_strip_stale_suppressed_close_runs_before_top_level_boundaries = nullptr;
    LuaQuestValidatedRepairFunction
        try_restore_structured_close_runs_before_top_level_boundaries = nullptr;
    LuaQuestSimpleRepairFunction try_strip_redundant_end_before_top_level_boundary = nullptr;
};
bool RunSharedQuestWaitRepairStage(std::string& decompiled_source,
                                   std::string_view validation_error,
                                   std::string* note,
                                   const SharedQuestWaitRepairStageOptions& options,
                                   const LuaQuestRepairStageOps& ops);
bool RunSharedQuestOwnerNormalizationStage(std::string& decompiled_source,
                                           std::string* note,
                                           const LuaQuestRepairStageOps& ops);
bool RunPreCleanOwnerAndReferenceCleanupStage(std::string& decompiled_source,
                                              std::string* note,
                                              const LuaQuestRepairStageOps& ops);
bool RunOrderedOwnerAndCloseCleanupStage(std::string& decompiled_source,
                                         std::string_view validation_error,
                                         std::string* note,
                                         const LuaQuestRepairStageOps& ops);
bool TryStripRedundantEndBeforeTopLevelBoundary(std::string& decompiled_source,
                                                std::string* note);
bool TryNormalizeQuestOwnerContinuationCleanup(std::string& decompiled_source,
                                               std::string* note);
struct LuaRepairStageCallbacks {
    void* context = nullptr;
    bool (*run_early_structural_preclean)(void*) = nullptr;
    bool (*run_quest_wait_repairs)(void*) = nullptr;
    bool (*run_owner_cleanup)(void*) = nullptr;
};
bool RunLuaRepairStageSequence(const LuaRepairStageCallbacks& callbacks);
bool ParseSimpleRenderedAssignment(std::string_view line,
                                   std::string* indent_out,
                                   std::string* lhs_out,
                                   std::string* rhs_out);
enum class NativeCandidateFinalizeKind {
    kInvalid,
    kExact,
    kNonexact,
};
struct NativeCandidateFinalizeResult {
    NativeCandidateFinalizeKind kind = NativeCandidateFinalizeKind::kInvalid;
    bool exact_parity_required = false;
};
std::string GetLuaLeadingIndent(std::string_view line);
bool IsBareLuaCallSuffix(std::string_view text);
bool TryRewriteImmediateAnonymousFunctionInvocations(std::string& decompiled_source,
                                                     std::string* note);
bool TryNameOrphanTopLevelAnonymousFunctionCalls(std::string& decompiled_source,
                                                 std::string_view validation_error,
                                                 std::string* note);
bool TryRewriteAnonymousFunctionCallThroughNamedLocal(std::string& decompiled_source,
                                                      std::string_view validation_error,
                                                      std::string* note);
bool TryRestoreStrandedTopLevelHelperFunctionBlocks(std::string& decompiled_source,
                                                    std::string_view validation_error,
                                                    std::string* note);
bool TryRewriteBareMethodReferenceConstructorAssignments(std::string& decompiled_source,
                                                         std::string* note);
bool TryRewriteSplitMethodReferenceSelfCalls(std::string& decompiled_source, std::string* note);
bool TryMergeSplitLocalMultiResultAssignments(std::string& decompiled_source,
                                              std::string* note);
bool TryRewriteBareMethodReferenceAssignments(std::string& decompiled_source, std::string* note);
bool TryRewriteInvalidMethodFieldAssignments(std::string& decompiled_source,
                                             std::string* note);
bool TryRepairValidatedTopLevelFunctionBoundaryBleed(std::string& decompiled_source,
                                                     std::string* note);
bool TryStripTopLevelLeakageAfterFunction(std::string& decompiled_source,
                                          std::string_view validation_error,
                                          std::string* note);
bool TryRewriteDanglingYieldElseWaitLoops(std::string& decompiled_source, std::string* note);
bool TryBreakRepeatedWaitElseContinuations(std::string& decompiled_source, std::string* note);
bool TryBreakEmptyThenYieldElseContinuations(std::string& decompiled_source,
                                             std::string* note);
bool TrySplitDuplicatedElseWaitLadders(std::string& decompiled_source, std::string* note);
bool TryRewriteRepeatIfYieldElseContinuations(std::string& decompiled_source,
                                              std::string* note);
bool TryRewriteImmediateYieldElseWaitLoops(std::string& decompiled_source, std::string* note);
bool TryStripMalformedRepeatWaitWrappers(std::string& decompiled_source,
                                         std::string* note);
bool TryRewriteBalancedOuterTrueGuardedWaitBlocks(std::string& decompiled_source,
                                                  std::string* note);
bool TryRewriteNestedRepeatedWaitContinuations(std::string& decompiled_source,
                                               std::string* note);
bool TryBreakSuppressedElseWaitContinuations(std::string& decompiled_source,
                                             std::string* note);
bool TryRewriteSuppressedElseSentinelWhileLoops(std::string& decompiled_source,
                                                std::string* note);
bool TryRewriteYieldTrappedWaitContinuations(std::string& decompiled_source,
                                             std::string* note);
bool TryLiftTrueWrappedWaitLoopContinuations(std::string& decompiled_source,
                                             std::string* note);
bool TryLiftMalformedYieldWaitContinuations(std::string& decompiled_source,
                                            std::string* note);
bool TryFlattenDegenerateTrueWaitWrappers(std::string& decompiled_source,
                                          std::string* note);
bool TryLiftTrailingYieldAcrossSuppressedCloseRun(std::string& decompiled_source,
                                                  std::string* note);
bool TryCollapseSuppressedElseYieldTailStateLoops(std::string& decompiled_source,
                                                  std::string* note);
bool TrySplitRepeatedWaitCollectionLoops(std::string& decompiled_source,
                                         std::string* note);
bool TrySimplifyRenderedWaitLoopPatterns(std::string& decompiled_source, std::string* note);
bool TryRewriteDoReturnEndBranches(std::string& decompiled_source,
                                   std::string_view validation_error,
                                   std::string* note);
bool TryStripStandaloneDoReturnEndArtifacts(std::string& decompiled_source,
                                            std::string* note);
bool TryStripDanglingDoBeforeBoundary(std::string& decompiled_source,
                                      std::string_view validation_error,
                                      std::string* note);
bool TryStripDanglingDoInsideTrueLoop(std::string& decompiled_source,
                                      std::string_view validation_error,
                                      std::string* note);
bool TryStripStaleSuppressedCloseRunsBeforeTopLevelBoundaries(
    std::string& decompiled_source,
    std::string* note);
bool TryRestoreStructuredCloseRunsBeforeTopLevelBoundaries(
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note);
bool TryAppendMissingEnds(std::string& decompiled_source,
                          std::string_view validation_error,
                          std::string* note);
bool TryAppendMissingUntil(std::string& decompiled_source,
                           std::string_view validation_error,
                           std::string* note);
bool TryBalanceOpenBlocks(std::string& decompiled_source,
                          std::string_view validation_error,
                          std::string* note);
bool TryMaterializeMissingEndWarnings(std::string& decompiled_source, std::string* note);
bool TryMaterializeCommentedControlLines(std::string& decompiled_source, std::string* note);
bool TryCommentDanglingControlLinesAroundInvalidBlocks(std::string& decompiled_source,
                                                       std::string* note);
bool TryStripDanglingDoAfterUntil(std::string& decompiled_source, std::string* note);
bool TryStripDanglingDoBeforeElse(std::string& decompiled_source, std::string* note);
bool TryStripDanglingEndBeforeElse(std::string& decompiled_source, std::string* note);
bool TryRemoveExcessEndLines(std::string& decompiled_source,
                             std::string_view validation_error,
                             std::string* note);
bool TryStripEmptyElseBlocks(std::string& decompiled_source, std::string* note);
bool TryCollapseDuplicatedElseLines(std::string& decompiled_source,
                                    std::string_view validation_error,
                                    std::string* note);
bool TryCollapseNestedElseIfInsideClause(std::string& decompiled_source,
                                         std::string_view validation_error,
                                         std::string* note);
bool TryStripStandaloneLocalArtifactLines(std::string& decompiled_source, std::string* note);
bool TryInsertEndBeforeReportedLine(std::string& decompiled_source,
                                    std::string_view validation_error,
                                    std::string* note);
bool TryInsertEndBeforeElse(std::string& decompiled_source,
                            std::string_view validation_error,
                            std::string* note);
bool TryRepairNoLoopToBreakNearElse(std::string& decompiled_source,
                                    std::string_view validation_error,
                                    std::string* note);
bool TryRemovePrematureYieldLoopEndBeforeBreakElse(std::string& decompiled_source,
                                                   std::string_view validation_error,
                                                   std::string* note);
bool TryInsertEndBeforeReturn(std::string& decompiled_source,
                              std::string_view validation_error,
                              std::string* note);
bool TryInsertEndBeforeUntil(std::string& decompiled_source,
                             std::string_view validation_error,
                             std::string* note);
bool TryInsertUntilBeforeTrailingEnd(std::string& decompiled_source,
                                     std::string_view validation_error,
                                     std::string* note);
bool TryRewriteReportedDanglingRepeatLoop(std::string& decompiled_source,
                                          std::string_view validation_error,
                                          std::string* note);
bool TryRewriteDanglingRepeatLoops(std::string& decompiled_source, std::string* note);
bool TryRewriteWhileTrueToRepeat(std::string& decompiled_source,
                                 std::string_view validation_error,
                                 std::string* note);
bool TryCloseReportedTopLevelFunctionBeforeSiblingBoundary(
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note);
bool TryInsertEndBeforeMalformedTopLevelBoundary(std::string& decompiled_source,
                                                 std::string_view validation_error,
                                                 std::string* note);
bool TryMoveTrailingEndsBeforeReportedBoundary(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note);
std::string ComputeStableBytecodeIdentity(std::string_view bytes);
struct ParsedChunkCacheEntry {
    bool attempted = false;
    bool success = false;
    std::string parse_error;
    refii::lua::Lua51Chunk chunk;
    refii::lua::Lua51ChunkStats chunk_stats;
    refii::lua::Lua51OperationSummary operation_summary;
    refii::lua::Lua51ControlFlowSummary control_flow_summary;
};
ParsedChunkCacheEntry& GetParsedChunkCacheEntry(const std::vector<char>& input_bytes);
bool CompileLuaSourceToChunkCached(std::string_view source,
                                   std::string_view chunk_name,
                                   refii::lua::audit::CompiledLuaChunkInfo* output);
void ResetTransientSourceDecompileCaches();
std::string ValidateLuaSource(std::string_view source, std::string_view chunk_name);
std::string ValidateLuaRepairSource(std::string_view source);
std::string SummarizeSyntaxError(std::string_view raw_error);
std::size_t ExtractLuaErrorLine(std::string_view raw_error);

struct LuaMutableTextContext {
    LuaMutableTextContext() = default;
    explicit LuaMutableTextContext(std::string initial_source) {
        SetSource(std::move(initial_source));
    }

    void SetSource(std::string text) {
        if (!source_dirty && !lines_dirty && source == text) {
            return;
        }
        source = std::move(text);
        lines.clear();
        had_trailing_newline = false;
        lines_dirty = true;
        source_dirty = false;
        ClearCaches();
    }

    void SetSourceView(std::string_view text) {
        if (!source_dirty && !lines_dirty && source == text) {
            return;
        }
        source.assign(text.data(), text.size());
        lines.clear();
        had_trailing_newline = false;
        lines_dirty = true;
        source_dirty = false;
        ClearCaches();
    }

    void SetLines(std::vector<std::string> next_lines, bool trailing_newline) {
        lines = std::move(next_lines);
        had_trailing_newline = trailing_newline;
        source.clear();
        source_dirty = true;
        lines_dirty = false;
        ClearCaches();
    }

    const std::vector<std::string>& EnsureLines() {
        if (!lines_dirty) {
            return lines;
        }
        lines = SplitLines(source, &had_trailing_newline);
        lines_dirty = false;
        source_dirty = false;
        scope_paths_cache.reset();
        return lines;
    }

    std::vector<std::string>* EnsureMutableLines() {
        EnsureLines();
        return &lines;
    }

    const std::string& EnsureSource() {
        if (!source_dirty) {
            return source;
        }
        source = JoinLines(lines, had_trailing_newline);
        source_dirty = false;
        lines_dirty = false;
        validation_cache.clear();
        scope_paths_cache.reset();
        return source;
    }

    std::string* EnsureMutableSource() {
        EnsureSource();
        return &source;
    }

    bool HadTrailingNewline() const {
        return had_trailing_newline;
    }

    void MarkLinesMutated() {
        source_dirty = true;
        lines_dirty = false;
        validation_cache.clear();
        scope_paths_cache.reset();
    }

    void MarkSourceMutated() {
        lines_dirty = true;
        source_dirty = false;
        validation_cache.clear();
        scope_paths_cache.reset();
    }

    const std::vector<std::vector<std::size_t>>& GetScopePaths() {
        EnsureLines();
        if (!scope_paths_cache.has_value()) {
            scope_paths_cache.emplace();
            auto& scope_paths = *scope_paths_cache;
            scope_paths.assign(lines.size(), {});
            std::vector<std::size_t> scope_stack;
            for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
                scope_paths[line_index] = scope_stack;
                const std::string_view trimmed =
                    TrimWhitespace(StripLineComment(lines[line_index]));
                if (trimmed.empty()) {
                    continue;
                }
                if (trimmed == "end" || trimmed.starts_with("until ")) {
                    if (!scope_stack.empty()) {
                        scope_stack.pop_back();
                    }
                    continue;
                }
                if (trimmed == "else" || trimmed.starts_with("elseif ")) {
                    if (!scope_stack.empty()) {
                        scope_stack.back() = line_index;
                    }
                    continue;
                }
                const bool begins_scope =
                    (trimmed.starts_with("if ") && trimmed.ends_with(" then")) ||
                    (trimmed.starts_with("for ") && trimmed.ends_with(" do")) ||
                    (trimmed.starts_with("while ") && trimmed.ends_with(" do")) ||
                    trimmed == "do" ||
                    trimmed == "repeat" ||
                    trimmed.starts_with("function ") ||
                    trimmed.starts_with("local function ");
                if (begins_scope) {
                    scope_stack.push_back(line_index);
                }
            }
        }
        return *scope_paths_cache;
    }

    std::string ValidateCached(std::string_view chunk_label) {
        const std::string& current_source = EnsureSource();
        const std::size_t source_hash = std::hash<std::string_view>{}(current_source);
        std::string key;
        key.reserve(chunk_label.size() + 32);
        key.append(chunk_label.data(), chunk_label.size());
        key.push_back('#');
        key.append(std::to_string(source_hash));
        const auto it = validation_cache.find(key);
        if (it != validation_cache.end()) {
            return it->second;
        }
        std::string result = ValidateLuaSource(current_source, chunk_label);
        validation_cache.emplace(std::move(key), result);
        return result;
    }

    std::string ValidateRepairCached() {
        const std::string& current_source = EnsureSource();
        const std::size_t source_hash = std::hash<std::string_view>{}(current_source);
        std::string key = "repair#";
        key.append(std::to_string(source_hash));
        const auto it = validation_cache.find(key);
        if (it != validation_cache.end()) {
            return it->second;
        }
        std::string result = ValidateLuaRepairSource(current_source);
        validation_cache.emplace(std::move(key), result);
        return result;
    }

    void ClearCaches() {
        validation_cache.clear();
        scope_paths_cache.reset();
    }

private:
    std::string source;
    std::vector<std::string> lines;
    bool had_trailing_newline = false;
    bool lines_dirty = true;
    bool source_dirty = false;
    std::optional<std::vector<std::vector<std::size_t>>> scope_paths_cache;
    std::unordered_map<std::string, std::string> validation_cache;
};

struct SyntaxErrorDescriptor {
    std::string summary;
    std::size_t line = 0;
};

struct DecompiledMarkerSummary {
    std::size_t decompiler_error_count = 0;
    std::size_t repeated_control_flow_count = 0;
    std::size_t suspicious_while_true_count = 0;
    std::size_t suspicious_until_true_count = 0;
    std::size_t for_generator_count = 0;
    std::size_t suppressed_control_count = 0;
};

struct LuaOutputQualitySummary {
    DecompiledMarkerSummary marker_summary;
    std::size_t undefined_local_warning_count = 0;
    std::size_t missing_end_warning_count = 0;
    std::size_t do_return_end_count = 0;
};

SyntaxErrorDescriptor DescribeSyntaxError(std::string_view raw_error);
void AppendSyntaxErrorNotes(std::string* note,
                            std::string_view summary_key,
                            std::string_view line_key,
                            std::string_view raw_error);
bool IsValidationOutcomeBetter(std::string_view baseline_error,
                               std::string_view candidate_error);
DecompiledMarkerSummary SummarizeDecompilerMarkers(std::string_view source);
LuaOutputQualitySummary SummarizeLuaOutputQuality(std::string_view source);
bool HasBlockingLuaOutputQualityIssues(const LuaOutputQualitySummary& summary);
bool IsLuaOutputQualityNoWorse(const LuaOutputQualitySummary& baseline,
                               const LuaOutputQualitySummary& candidate);
bool IsLuaOutputQualityBetter(const LuaOutputQualitySummary& baseline,
                              const LuaOutputQualitySummary& candidate);
bool IsDecompilerMarkerSummaryBetter(const DecompiledMarkerSummary& baseline,
                                     const DecompiledMarkerSummary& candidate);
std::string DescribeLuaOutputQuality(const LuaOutputQualitySummary& summary);
bool LooksLikeTopLevelBoundary(std::string_view trimmed);
bool TryInsertEndsBeforeTopLevelBoundaries(std::string& decompiled_source,
                                           std::string_view validation_error,
                                           std::string* note);
bool TryRemoveEndsBeforeTopLevelBoundaries(std::string& decompiled_source,
                                           std::string_view validation_error,
                                           std::string* note);

enum class SyntaxRepairAttemptResult {
    kNoChange,
    kChanged,
    kValidated,
};

enum class RenderedLuaCleanupProfile {
    kValidatedOutput,
    kAggressiveCandidateRefinement,
};

struct OrderedSyntaxRepairPassOptions {
    bool include_top_level_leakage_cleanup = false;
    bool include_member_reference_repairs = false;
    bool include_for_generator_repairs = false;
    bool include_generator_comment_repairs = false;
    bool include_while_true_to_repeat = false;
    bool include_insert_end_before_return = false;
    bool include_insert_end_before_until = false;
    bool include_insert_until_before_trailing_end = false;
    bool include_insert_ends_before_top_level_boundaries = false;
};

SyntaxRepairAttemptResult TryRunAnchoredValidatedSyntaxRepairs(
    std::string& decompiled_source,
    std::string& validation_error,
    std::string_view validation_label,
    std::string* note);
bool ApplySharedRenderedLuaCleanup(std::vector<std::string>* lines,
                                   RenderedLuaCleanupProfile profile);
bool DebugApplyHeaderComparisonParitySwap(std::string* source);
bool TryRepairReportedFunctionsFromBytecodeIteratively(const std::vector<char>& input_bytes,
                                                       std::string& decompiled_source,
                                                       std::string* validation_error,
                                                       std::string* note,
                                                       std::string_view note_prefix);
bool TryReplaceDecompilerErrorFunctions(const std::vector<char>& input_bytes,
                                        std::string& decompiled_source,
                                        std::string* note,
                                        bool allow_full_main_chunk_replacement,
                                        const LuaWorkspaceDecompileOptions* options = nullptr,
                                        const std::filesystem::path* input_path = nullptr);
bool TryReplaceWithValidatedNativeMainChunk(
    const std::vector<char>& input_bytes,
    std::string& decompiled_source,
    std::string* note,
    const LuaWorkspaceDecompileOptions* options = nullptr,
    const std::filesystem::path* input_path = nullptr,
    std::string_view preferred_render_mode = {});
bool ShouldRequireExactParityForInput(const std::filesystem::path& relative_input,
                                      const std::filesystem::path* input_path,
                                      const std::vector<char>& input_bytes,
                                      const LuaWorkspaceDecompileOptions* options = nullptr);
NativeCandidateFinalizeResult TryFinalizeNativeCandidateOutput(
    const std::filesystem::path& relative_input,
    std::string final_source,
    const std::vector<char>& input_bytes,
    const std::filesystem::path& output_path,
    const std::filesystem::path& failed_output_path,
    const LuaWorkspaceDecompileOptions* options,
    const std::filesystem::path* input_path,
    std::string* note,
    const char* note_prefix);
NativeCandidateFinalizeResult ClassifyExistingNativeCandidateOutput(
    const std::filesystem::path& relative_input,
    const std::filesystem::path& output_path,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    const LuaWorkspaceDecompileOptions* options,
    std::string* note,
    const char* note_prefix);
bool ValidateAndRepairDecompiledOutput(const std::filesystem::path& relative_input,
                                       std::string_view source_name,
                                       const std::vector<char>& input_bytes,
                                       const LuaWorkspaceDecompileOptions* options,
                                       const std::filesystem::path* input_path,
                                       const std::filesystem::path& output_path,
                                       const std::filesystem::path& failed_output_path,
                                       std::string* note);

}  // namespace refii::lua::internal
