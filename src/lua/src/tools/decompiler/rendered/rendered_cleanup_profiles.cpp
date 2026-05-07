#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua {

bool HasRenderedLuaQuestStructuralCleanupSignals(const std::vector<std::string>& lines) {
    std::size_t loop_markers = 0;
    const std::size_t scan_limit = std::min<std::size_t>(lines.size(), 6000);
    for (std::size_t index = 0; index < scan_limit; ++index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment(lines[index]));
        if (trimmed.empty()) {
            continue;
        }
        if (trimmed.find("CurrentState") != std::string_view::npos ||
            trimmed.find("MessageEvents.") != std::string_view::npos ||
            trimmed.find("coroutine.yield") != std::string_view::npos ||
            trimmed.find("-- UNHANDLED IF") != std::string_view::npos ||
            trimmed.find("while true do") != std::string_view::npos) {
            return true;
        }
        if (trimmed.find("while ") != std::string_view::npos ||
            trimmed.find("repeat") != std::string_view::npos ||
            trimmed.find("until ") != std::string_view::npos) {
            ++loop_markers;
            if (loop_markers >= 4) {
                return true;
            }
        }
    }
    return false;
}

bool ApplyRenderedLuaQuestStructuralCleanupProfile(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    if (!HasRenderedLuaQuestStructuralCleanupSignals(*lines)) {
        return false;
    }

    BeginRenderedLuaCleanupProfile("quest");
    bool changed = false;

    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDependentAssignedRepeatedConditions",
                                       SimplifyRenderedLuaDependentAssignedRepeatedConditions,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaRepeatedWhileTrueWrappers",
                                       SimplifyRenderedLuaRepeatedWhileTrueWrappers,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledStateIfComments",
                                       SimplifyRenderedLuaUnhandledStateIfComments,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledIfCommentBranchChains",
                                       SimplifyRenderedLuaUnhandledIfCommentBranchChains,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledIfCommentLeadingSuffixes",
                                       SimplifyRenderedLuaUnhandledIfCommentLeadingSuffixes,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledIfCommentNestedTailChains",
                                       SimplifyRenderedLuaUnhandledIfCommentNestedTailChains,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledIfCommentNilGuardSplits",
                                       SimplifyRenderedLuaUnhandledIfCommentNilGuardSplits,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledSparseNilGuardBranches",
                                       SimplifyRenderedLuaUnhandledSparseNilGuardBranches,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledOuterOrNestedElseIfChains",
                                       SimplifyRenderedLuaUnhandledOuterOrNestedElseIfChains,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledFlaggedElseIfChains",
                                       SimplifyRenderedLuaUnhandledFlaggedElseIfChains,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledStateYieldCommentLoops",
                                       SimplifyRenderedLuaUnhandledStateYieldCommentLoops,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledCurrentStateLeadingGuards",
                                       SimplifyRenderedLuaUnhandledCurrentStateLeadingGuards,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(
        lines,
        "SimplifyRenderedLuaUnhandledNestedCurrentStateElseIfBranches",
        SimplifyRenderedLuaUnhandledNestedCurrentStateElseIfBranches,
        &changed,
        "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(
        lines,
        "SimplifyRenderedLuaUnhandledNestedCurrentStateGuardBranches",
        SimplifyRenderedLuaUnhandledNestedCurrentStateGuardBranches,
        &changed,
        "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedCurrentStateElseIfLadders",
                                       SimplifyRenderedLuaNestedCurrentStateElseIfLadders,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedElseIfChains",
                                       SimplifyRenderedLuaNestedElseIfChains,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedElseIfChainsGeneric",
                                       SimplifyRenderedLuaNestedElseIfChainsGeneric,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTerminalElseIfNestedIfBranches",
                                       SimplifyRenderedLuaTerminalElseIfNestedIfBranches,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaElseIfBeforeElseTails",
                                       SimplifyRenderedLuaElseIfBeforeElseTails,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaCurrentStateMissingGuardElseBodies",
                                       SimplifyRenderedLuaCurrentStateMissingGuardElseBodies,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnhandledRetargetBreakYieldLoops",
                                       SimplifyRenderedLuaUnhandledRetargetBreakYieldLoops,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaMessagePollingLoops",
                                       SimplifyRenderedLuaMessagePollingLoops,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaPrecheckedGuardedWaitLoops",
                                       SimplifyRenderedLuaPrecheckedGuardedWaitLoops,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDirectBreakYieldLoops",
                                       SimplifyRenderedLuaDirectBreakYieldLoops,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaLoopGuardedYieldElse",
                                       SimplifyRenderedLuaLoopGuardedYieldElse,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaYieldElseWaitLoops",
                                       SimplifyRenderedLuaYieldElseWaitLoops,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingOuterTrueWaitWrappers",
                                       SimplifyRenderedLuaDanglingOuterTrueWaitWrappers,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingElseGuardedWaitLoops",
                                       SimplifyRenderedLuaDanglingElseGuardedWaitLoops,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaIndexedAliveCleanupLoops",
                                       SimplifyRenderedLuaIndexedAliveCleanupLoops,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaLoopTrailingReturnElse(early)",
                                       SimplifyRenderedLuaLoopTrailingReturnElse,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaConditionalCallFallbacks",
                                       SimplifyRenderedLuaConditionalCallFallbacks,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaBooleanCoercionSelections",
                                       SimplifyRenderedLuaBooleanCoercionSelections,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaBooleanCoercionReturns",
                                       SimplifyRenderedLuaBooleanCoercionReturns,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "WrapRenderedLuaReturnedCallsForExactParity",
                                       WrapRenderedLuaReturnedCallsForExactParity,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelNamespaceTableAliasChains",
                                       SimplifyRenderedLuaTopLevelNamespaceTableAliasChains,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingCallbackRegistrations",
                                       SimplifyRenderedLuaDanglingCallbackRegistrations,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaLoopActionBuilderAliases",
                                       NormalizeRenderedLuaLoopActionBuilderAliases,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingCallAssignmentHeads",
                                       SimplifyRenderedLuaDanglingCallAssignmentHeads,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingDestroyReplayCalls",
                                       SimplifyRenderedLuaDanglingDestroyReplayCalls,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaPendingRegisterMethodResultCarries",
                                       SimplifyRenderedLuaPendingRegisterMethodResultCarries,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaInvalidEmptyStringMethodReceivers",
                                       SimplifyRenderedLuaInvalidEmptyStringMethodReceivers,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaAssignedFunctionDeclarations",
                                       SimplifyRenderedLuaAssignedFunctionDeclarations,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaQuestManagerCameraHelperDefaults",
                                       SimplifyRenderedLuaQuestManagerCameraHelperDefaults,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaInvalidAnonymousCallArtifacts",
                                       SimplifyRenderedLuaInvalidAnonymousCallArtifacts,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaGamefaceFunctionPlaceholders",
                                       SimplifyRenderedLuaGamefaceFunctionPlaceholders,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingEmptyFunctionPlaceholders",
                                       SimplifyRenderedLuaDanglingEmptyFunctionPlaceholders,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingIoOpenReadClose",
                                       SimplifyRenderedLuaDanglingIoOpenReadClose,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingLocalFunctionAliases",
                                       SimplifyRenderedLuaDanglingLocalFunctionAliases,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaSplitLocalMemberMultiAssignments",
                                       SimplifyRenderedLuaSplitLocalMemberMultiAssignments,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaInsertMissingEndsBeforeTopLevelAssignments",
                                       SimplifyRenderedLuaInsertMissingEndsBeforeTopLevelAssignments,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaFinalFunctionPlaceholderCalls",
                                       SimplifyRenderedLuaFinalFunctionPlaceholderCalls,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaResidualNamedFunctionCallPlaceholders",
                                       SimplifyRenderedLuaResidualNamedFunctionCallPlaceholders,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaInvalidNumericForLoopHeads",
                                       SimplifyRenderedLuaInvalidNumericForLoopHeads,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaInvalidGenericForLoopHeads",
                                       SimplifyRenderedLuaInvalidGenericForLoopHeads,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "RemoveRenderedLuaEmptyIfBlocks",
                                       RemoveRenderedLuaEmptyIfBlocks,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaResidualNamedFunctionCallPlaceholdersLate",
                                       SimplifyRenderedLuaResidualNamedFunctionCallPlaceholders,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaResidualFunctionPlaceholdersLate",
                                       SimplifyRenderedLuaResidualFunctionPlaceholdersLate,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedGuardedDuplicateBodies",
                                       SimplifyRenderedLuaNestedGuardedDuplicateBodies,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedGuardedFallbackDuplicateBodies",
                                       SimplifyRenderedLuaNestedGuardedFallbackDuplicateBodies,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedGuardedAssignmentFallbackElse",
                                       SimplifyRenderedLuaNestedGuardedAssignmentFallbackElse,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaEmptyElseBranches",
                                       SimplifyRenderedLuaEmptyElseBranches,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaOuterElseBreakIntoInnerElseBreak",
                                       SimplifyRenderedLuaOuterElseBreakIntoInnerElseBreak,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaAdjacentSimpleSiblingElseIfChains",
                                       SimplifyRenderedLuaAdjacentSimpleSiblingElseIfChains,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTailElseIfBreakIntoNestedIf",
                                       SimplifyRenderedLuaTailElseIfBreakIntoNestedIf,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaGuardedTailBreakIntoPreviousIf",
                                       SimplifyRenderedLuaGuardedTailBreakIntoPreviousIf,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaImmediateMissingElseAssignments",
                                       SimplifyRenderedLuaImmediateMissingElseAssignments,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaImmediateMissingElseTrailingTableCalls",
                                       SimplifyRenderedLuaImmediateMissingElseTrailingTableCalls,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedDuplicateElseAssignments",
                                       SimplifyRenderedLuaNestedDuplicateElseAssignments,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaPostIfFallbackElse",
                                       SimplifyRenderedLuaPostIfFallbackElse,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTrailingReturnFallbackElse",
                                       SimplifyRenderedLuaTrailingReturnFallbackElse,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaMissingElseDuplicateTails",
                                       SimplifyRenderedLuaMissingElseDuplicateTails,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedMissingElseDuplicateTails",
                                       SimplifyRenderedLuaNestedMissingElseDuplicateTails,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaEmptyLoopExitGuards",
                                       SimplifyRenderedLuaEmptyLoopExitGuards,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaThreadRegistrationSiblingFunctionEnds",
                                       SimplifyRenderedLuaThreadRegistrationSiblingFunctionEnds,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingEofClosures",
                                       SimplifyRenderedLuaDanglingEofClosures,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaEmptyElseBranches(final)",
                                       SimplifyRenderedLuaEmptyElseBranches,
                                       &changed,
                                       "quest_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "RemoveRenderedLuaEmptyIfBlocks(final)",
                                       RemoveRenderedLuaEmptyIfBlocks,
                                       &changed,
                                       "quest_cleanup");

    return changed;
}

bool ExpandRenderedLuaTopLevelSetterTableBootstrapCalls(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines->size(); ++i) {
            if (!TrimLuaWhitespace((*lines)[i]).empty()) {
                return i;
            }
        }
        return lines->size();
    };

    const auto trim_trailing_comma = [](std::string line) {
        const std::size_t last_non_ws = line.find_last_not_of(" \t");
        if (last_non_ws != std::string::npos && line[last_non_ws] == ',') {
            line.erase(last_non_ws, 1);
        }
        return line;
    };

    const auto compute_brace_delta = [](std::string_view line) -> int {
        int delta = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        for (std::size_t index = 0; index < line.size(); ++index) {
            if (!in_single_quote && !in_double_quote && index + 1 < line.size() &&
                line[index] == '-' && line[index + 1] == '-') {
                break;
            }

            if (in_single_quote) {
                if (line[index] == '\\' && index + 1 < line.size()) {
                    ++index;
                } else if (line[index] == '\'') {
                    in_single_quote = false;
                }
                continue;
            }

            if (in_double_quote) {
                if (line[index] == '\\' && index + 1 < line.size()) {
                    ++index;
                } else if (line[index] == '"') {
                    in_double_quote = false;
                }
                continue;
            }

            if (line[index] == '\'') {
                in_single_quote = true;
                continue;
            }
            if (line[index] == '"') {
                in_double_quote = true;
                continue;
            }
            if (line[index] == '{') {
                ++delta;
            } else if (line[index] == '}') {
                --delta;
            }
        }
        return delta;
    };

    const auto parse_setter_table_call_start =
        [](std::string_view line,
           std::string* indent,
           std::string* callee,
           std::string* target_name) -> bool {
        const std::string_view stripped = StripLuaLineComment(line);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (trimmed.empty()) {
            return false;
        }

        const std::size_t open_paren = trimmed.rfind('(');
        if (open_paren == std::string_view::npos) {
            return false;
        }

        const std::string_view callee_view = TrimLuaWhitespace(trimmed.substr(0, open_paren));
        const std::string_view argument_view =
            TrimLuaWhitespace(trimmed.substr(open_paren + 1));
        if (!IsRenderedSafePrefixExpression(callee_view) || argument_view != "{") {
            return false;
        }

        if (!callee_view.starts_with("Set") || callee_view.size() <= 3) {
            return false;
        }

        const std::string_view candidate_target = callee_view.substr(3);
        if (!IsValidLuaIdentifier(candidate_target) || IsLuaKeyword(candidate_target) ||
            IsRenderTemporaryName(candidate_target)) {
            return false;
        }

        if (indent != nullptr) {
            const std::size_t first_non_space = line.find_first_not_of(" \t");
            *indent = first_non_space == std::string_view::npos
                          ? std::string()
                          : std::string(line.substr(0, first_non_space));
        }
        if (callee != nullptr) {
            *callee = std::string(callee_view);
        }
        if (target_name != nullptr) {
            *target_name = std::string(candidate_target);
        }
        return true;
    };

    const auto parse_setter_table_call_end =
        [](std::string_view line, std::string* trailing_comment) -> bool {
        const std::string_view stripped = StripLuaLineComment(line);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (trimmed != "})") {
            return false;
        }

        if (trailing_comment != nullptr) {
            *trailing_comment =
                stripped.size() < line.size() ? std::string(line.substr(stripped.size()))
                                              : std::string();
        }
        return true;
    };

    bool changed = false;
    RunRenderedLuaCleanupPassLoop("ExpandRenderedLuaTopLevelSetterTableBootstrapCalls",
                                  32,
                                  [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            std::string indent;
            std::string callee;
            std::string target_name;
            if (!parse_setter_table_call_start((*lines)[line_index],
                                               &indent,
                                               &callee,
                                               &target_name)) {
                continue;
            }

            int brace_depth = compute_brace_delta((*lines)[line_index]);
            if (brace_depth != 1) {
                continue;
            }

            std::size_t table_end = lines->size();
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                brace_depth += compute_brace_delta((*lines)[probe]);
                if (brace_depth == 0) {
                    table_end = probe;
                    break;
                }
                if (brace_depth < 0) {
                    break;
                }
            }
            if (table_end >= lines->size()) {
                continue;
            }

            std::string trailing_comment;
            if (!parse_setter_table_call_end((*lines)[table_end], &trailing_comment)) {
                continue;
            }

            bool saw_self_reference = false;
            for (std::size_t probe = line_index + 1; probe < table_end; ++probe) {
                if (CountIdentifierUses((*lines)[probe], target_name) != 0) {
                    saw_self_reference = true;
                    break;
                }
            }
            if (!saw_self_reference) {
                continue;
            }

            std::vector<std::string> replacement_lines;
            replacement_lines.push_back(indent + target_name + " = {}");

            std::size_t cursor = next_nonempty(line_index + 1);
            std::size_t entry_count = 0;
            int expected_array_index = 1;
            bool failed = false;
            while (cursor < table_end) {
                const std::string_view entry_trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor]));
                if (entry_trimmed.empty()) {
                    cursor = next_nonempty(cursor + 1);
                    continue;
                }

                std::string access_suffix;
                std::string rhs_expression;
                std::size_t entry_end = cursor;

                const std::size_t equal = entry_trimmed.find('=');
                if (equal == std::string_view::npos) {
                    access_suffix = "[" + std::to_string(expected_array_index) + "]";
                    rhs_expression = trim_trailing_comma(std::string(entry_trimmed));
                    if (rhs_expression.empty()) {
                        failed = true;
                        break;
                    }
                    if (rhs_expression == "{") {
                        entry_end = FindRenderedTableLiteralEnd(*lines, cursor);
                    } else if (rhs_expression.starts_with("function(") ||
                               rhs_expression.starts_with("function ")) {
                        entry_end = FindRenderedFunctionBlockEnd(*lines, cursor);
                    }
                    ++expected_array_index;
                } else {
                    const std::string_view lhs =
                        TrimLuaWhitespace(entry_trimmed.substr(0, equal));
                    const std::string_view rhs =
                        TrimLuaWhitespace(entry_trimmed.substr(equal + 1));
                    if (lhs.empty() || rhs.empty()) {
                        failed = true;
                        break;
                    }

                    if (IsValidLuaIdentifier(lhs)) {
                        access_suffix = "." + std::string(lhs);
                    } else if (lhs.front() == '[' && lhs.back() == ']') {
                        access_suffix = std::string(lhs);
                    } else {
                        failed = true;
                        break;
                    }

                    rhs_expression = std::string(rhs);
                    if (rhs_expression == "{") {
                        entry_end = FindRenderedTableLiteralEnd(*lines, cursor);
                    } else if (rhs_expression.starts_with("function(") ||
                               rhs_expression.starts_with("function ")) {
                        entry_end = FindRenderedFunctionBlockEnd(*lines, cursor);
                    }
                }

                if (entry_end >= table_end || entry_end < cursor) {
                    failed = true;
                    break;
                }

                replacement_lines.push_back(indent + target_name + access_suffix + " = " +
                                            trim_trailing_comma(rhs_expression));
                for (std::size_t body_line = cursor + 1; body_line <= entry_end; ++body_line) {
                    std::string moved_line = (*lines)[body_line];
                    if (body_line == entry_end) {
                        moved_line = trim_trailing_comma(std::move(moved_line));
                    }
                    replacement_lines.push_back(std::move(moved_line));
                }

                ++entry_count;
                cursor = next_nonempty(entry_end + 1);
            }

            if (failed || entry_count == 0) {
                continue;
            }

            replacement_lines.push_back(indent + callee + "(" + target_name + ")" +
                                        trailing_comment);

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(table_end + 1));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          replacement_lines.begin(),
                          replacement_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }

        return pass_changed;
    });

    return changed;
}

bool ApplyRenderedLuaBootstrapCleanupProfile(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    BeginRenderedLuaCleanupProfile("bootstrap");
    bool changed = false;
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaImpossibleAsserts",
                                       SimplifyRenderedLuaImpossibleAsserts,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNamedNilAsserts",
                                       SimplifyRenderedLuaNamedNilAsserts,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaBooleanAssertConditions",
                                       SimplifyRenderedLuaBooleanAssertConditions,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaImmediateAssignedAssertReuses",
                                       SimplifyRenderedLuaImmediateAssignedAssertReuses,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaEmptyIfAssertGuards",
                                       SimplifyRenderedLuaEmptyIfAssertGuards,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaAssertConditionBlocks",
                                       SimplifyRenderedLuaAssertConditionBlocks,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaConditionHeaders",
                                       SimplifyRenderedLuaConditionHeaders,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaIntegralFloatLiterals",
                                       SimplifyRenderedLuaIntegralFloatLiterals,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaExpressionParentheses",
                                       SimplifyRenderedLuaExpressionParentheses,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDotAccess",
                                       SimplifyRenderedLuaDotAccess,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaCallParentheses",
                                       SimplifyRenderedLuaCallParentheses,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaLocalCallBootstrapGuards",
                                       SimplifyRenderedLuaLocalCallBootstrapGuards,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaSelfCallShadowLocals",
                                       SimplifyRenderedLuaSelfCallShadowLocals,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaExplicitSelfMethodArguments",
                                       SimplifyRenderedLuaExplicitSelfMethodArguments,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "RestoreRenderedLuaTopLevelSubclassBindings",
                                       RestoreRenderedLuaTopLevelSubclassBindings,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaSubclassBootstrapSetlistChains",
                                       SimplifyRenderedLuaSubclassBootstrapSetlistChains,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelMemberAliases",
                                       SimplifyRenderedLuaTopLevelMemberAliases,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelFunctionAliasAssignments",
                                       SimplifyRenderedLuaTopLevelFunctionAliasAssignments,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelTableLiteralAliasBuilders",
                                       SimplifyRenderedLuaTopLevelTableLiteralAliasBuilders,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingTopLevelIndexedAliases",
                                       SimplifyRenderedLuaDanglingTopLevelIndexedAliases,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTableLiteralCapturedTargetAliases",
                                       SimplifyRenderedLuaTableLiteralCapturedTargetAliases,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "RestoreRenderedLuaBaseObjectInitializers",
                                       RestoreRenderedLuaBaseObjectInitializers,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaNilTablePlaceholders",
                                       NormalizeRenderedLuaNilTablePlaceholders,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaMixedTableBuilderAliases",
                                       NormalizeRenderedLuaMixedTableBuilderAliases,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaForwardTableBuilderAliases",
                                       NormalizeRenderedLuaForwardTableBuilderAliases,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaOrphanedTopLevelMemberBuilderAliases",
                                       SimplifyRenderedLuaOrphanedTopLevelMemberBuilderAliases,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaFreshTargetTableSeeds",
                                       NormalizeRenderedLuaFreshTargetTableSeeds,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments",
                                       SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaSplitGenericForIteratorLoops",
                                       SimplifyRenderedLuaSplitGenericForIteratorLoops,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments",
                                       SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelObjectCarryAssignments",
                                       SimplifyRenderedLuaTopLevelObjectCarryAssignments,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaKnownFieldCarryAssignments",
                                       SimplifyRenderedLuaKnownFieldCarryAssignments,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaMultiResultMemberLhsTemps",
                                       SimplifyRenderedLuaMultiResultMemberLhsTemps,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaForwardLocalLiteralSeeds",
                                       SimplifyRenderedLuaForwardLocalLiteralSeeds,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaForwardCallArgumentLiteralSeeds",
                                       SimplifyRenderedLuaForwardCallArgumentLiteralSeeds,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaForwardInvalidTemporaryTableSeeds",
                                       SimplifyRenderedLuaForwardInvalidTemporaryTableSeeds,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaForwardSetlistPairSeeds",
                                       SimplifyRenderedLuaForwardSetlistPairSeeds,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaBalverineSequenceSetlistArtifacts",
                                       SimplifyRenderedLuaBalverineSequenceSetlistArtifacts,
                                       &changed,
                                       "bootstrap_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "ExpandRenderedLuaTopLevelSetterTableBootstrapCalls",
                                       ExpandRenderedLuaTopLevelSetterTableBootstrapCalls,
                                       &changed,
                                       "bootstrap_cleanup");
    return changed;
}

bool ApplyRenderedLuaExactParityNormalizationProfile(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    BeginRenderedLuaCleanupProfile("exact");
    bool changed = false;
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "RestoreRenderedLuaTopLevelSubclassBindings",
                                       RestoreRenderedLuaTopLevelSubclassBindings,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaSubclassBootstrapSetlistChains",
                                       SimplifyRenderedLuaSubclassBootstrapSetlistChains,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaScopedSequentialMemberCarries",
                                       SimplifyRenderedLuaScopedSequentialMemberCarries,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaScopedMemberAliases",
                                       SimplifyRenderedLuaScopedMemberAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelMemberAliases",
                                       SimplifyRenderedLuaTopLevelMemberAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaImmediateTemporaryAssignments",
                                       SimplifyRenderedLuaImmediateTemporaryAssignments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelImmediateResultAliases",
                                       SimplifyRenderedLuaTopLevelImmediateResultAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaLocalTableBuilderCallAliases",
                                       SimplifyRenderedLuaLocalTableBuilderCallAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelFunctionAliasAssignments",
                                       SimplifyRenderedLuaTopLevelFunctionAliasAssignments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelTableLiteralAliasBuilders",
                                       SimplifyRenderedLuaTopLevelTableLiteralAliasBuilders,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaDanglingTopLevelIndexedAliases",
                                       SimplifyRenderedLuaDanglingTopLevelIndexedAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTableLiteralCapturedTargetAliases",
                                       SimplifyRenderedLuaTableLiteralCapturedTargetAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaNilTablePlaceholders",
                                       NormalizeRenderedLuaNilTablePlaceholders,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaMixedTableBuilderAliases",
                                       NormalizeRenderedLuaMixedTableBuilderAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaForwardTableBuilderAliases",
                                       NormalizeRenderedLuaForwardTableBuilderAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaOrphanedTopLevelMemberBuilderAliases",
                                       SimplifyRenderedLuaOrphanedTopLevelMemberBuilderAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaFreshTargetTableSeeds",
                                       NormalizeRenderedLuaFreshTargetTableSeeds,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments",
                                       SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments",
                                       SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTopLevelObjectCarryAssignments",
                                       SimplifyRenderedLuaTopLevelObjectCarryAssignments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaKnownFieldCarryAssignments",
                                       SimplifyRenderedLuaKnownFieldCarryAssignments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaImmediateLocalLiteralSeeds",
                                       SimplifyRenderedLuaImmediateLocalLiteralSeeds,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaForwardLocalLiteralSeeds",
                                       SimplifyRenderedLuaForwardLocalLiteralSeeds,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTableBuilders",
                                       SimplifyRenderedLuaTableBuilders,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaLocalTableBuilderCallArguments",
                                       SimplifyRenderedLuaLocalTableBuilderCallArguments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaLocalTableLiteralCallAssignments",
                                       SimplifyRenderedLuaLocalTableLiteralCallAssignments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaImmediateTemporaryCallArgumentUses",
                                       SimplifyRenderedLuaImmediateTemporaryCallArgumentUses,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTableInitializerTemporaries",
                                       SimplifyRenderedLuaTableInitializerTemporaries,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTableLiteralTemporaries",
                                       SimplifyRenderedLuaTableLiteralTemporaries,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaStaleTargetTableAliases",
                                       NormalizeRenderedLuaStaleTargetTableAliases,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaImmediateIdentifierCarryLocals",
                                       SimplifyRenderedLuaImmediateIdentifierCarryLocals,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "ExpandRenderedLuaPredicateBooleanReturnsForParity",
                                       ExpandRenderedLuaPredicateBooleanReturnsForParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaInvalidEmptyStringMethodReceivers",
                                       SimplifyRenderedLuaInvalidEmptyStringMethodReceivers,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaAssignedFunctionDeclarations",
                                       SimplifyRenderedLuaAssignedFunctionDeclarations,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaQuestManagerCameraHelperDefaults",
                                       SimplifyRenderedLuaQuestManagerCameraHelperDefaults,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaConditionHeaders",
                                       SimplifyRenderedLuaConditionHeaders,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaIntegralFloatLiterals",
                                       SimplifyRenderedLuaIntegralFloatLiterals,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaRedundantGuardConditionParentheses",
                                       SimplifyRenderedLuaRedundantGuardConditionParentheses,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "HoistRenderedLuaSingleWhileElseTailsForExactParity",
                                       HoistRenderedLuaSingleWhileElseTailsForExactParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "CollapseRenderedLuaNestedSharedElseGuardsForExactParity",
                                       CollapseRenderedLuaNestedSharedElseGuardsForExactParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity",
                                       CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity",
                                       RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "UnwrapRenderedLuaReturnedCallsForExactParity",
                                       UnwrapRenderedLuaReturnedCallsForExactParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedCurrentStateElseIfLadders",
                                       SimplifyRenderedLuaNestedCurrentStateElseIfLadders,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedElseIfChains",
                                       SimplifyRenderedLuaNestedElseIfChains,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaNestedElseIfChainsGeneric",
                                       SimplifyRenderedLuaNestedElseIfChainsGeneric,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaTerminalElseIfNestedIfBranches",
                                       SimplifyRenderedLuaTerminalElseIfNestedIfBranches,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "ExpandRenderedLuaTerminalEmptyElseIfChainsForParity",
                                       ExpandRenderedLuaTerminalEmptyElseIfChainsForParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity",
                                       ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "ExpandRenderedLuaOrTableFallbackAssignmentsForParity",
                                       ExpandRenderedLuaOrTableFallbackAssignmentsForParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "ExpandRenderedLuaInlineLocalTableSeedFieldsForParity",
                                       ExpandRenderedLuaInlineLocalTableSeedFieldsForParity,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaExplicitSelfMethodArguments",
                                       SimplifyRenderedLuaExplicitSelfMethodArguments,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaStraightLineSingleUseTemps",
                                       SimplifyRenderedLuaStraightLineSingleUseTemps,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "SimplifyRenderedLuaUnusedDuplicatedTemps",
                                       SimplifyRenderedLuaUnusedDuplicatedTemps,
                                       &changed,
                                       "exact_cleanup");
    ApplyRenderedLuaCleanupProfileStep(lines,
                                       "NormalizeRenderedLuaWhitespace",
                                       NormalizeRenderedLuaWhitespace,
                                       &changed,
                                       "exact_cleanup");
    return changed;
}

}  // namespace refii::lua
