#pragma once

#include "tools/native_lua_chunk.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

bool IsLuaKeyword(std::string_view value);
bool IsValidLuaIdentifier(std::string_view value);
std::string EscapeLuaString(std::string_view value);
std::string RenderLuaNumber(double value);
std::string RenderLuaConstant(const Lua51Constant& constant);
std::string RenderLuaGlobal(std::string_view name);
std::string RenderLuaUpvalue(const Lua51Function& function, int index);

std::vector<std::string> SplitRenderedLines(std::string_view text);
std::string_view TrimLuaWhitespace(std::string_view value);
bool IsRenderTemporaryName(std::string_view value);
std::size_t FindIdentifierUse(std::string_view text,
                              std::string_view identifier,
                              std::size_t offset = 0);
std::size_t CountIdentifierUses(std::string_view text, std::string_view identifier);
bool IsTemporaryAssignmentLine(std::string_view line,
                               std::string* name,
                               std::string* expression);
bool IsLocalTemporaryAssignmentLine(std::string_view line,
                                    std::string* name,
                                    std::string* expression);
bool IsAssignmentToTemporary(std::string_view line, std::string_view identifier);
bool LocalizeRenderedLuaFirstAssignments(std::vector<std::string>* lines);
bool FindIdentifierUseInAssignmentRhs(std::string_view line,
                                      std::string_view identifier,
                                      std::size_t* absolute_found,
                                      std::size_t* use_count);
std::string WrapInlineExpression(std::string_view expression);
bool TryCanonicalizeRenderedExplicitSelfMethodCall(
    std::string_view callee,
    const std::vector<std::string>& arguments,
    std::string* rewritten_out);
bool HasPositiveLuaExpressionBalance(std::string_view expression,
                                     int* paren_balance_out = nullptr,
                                     int* brace_balance_out = nullptr);

bool SimplifyRenderedLuaTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaReturnTemporaries(std::vector<std::string>* lines);
std::size_t CountIdentifierKeyword(std::string_view line, std::string_view keyword);
bool StartsWithLuaKeyword(std::string_view text, std::string_view keyword);
std::string_view StripLuaLineComment(std::string_view line);
int GetRenderedLuaBlockDelta(std::string_view line);
bool IsRenderedFunctionStartLine(std::string_view line);
std::size_t FindRenderedFunctionBlockEnd(const std::vector<std::string>& lines,
                                         std::size_t start_line);

bool ParseRenderedTableInitializerLine(std::string_view line,
                                       std::string* indent,
                                       std::string* lhs_prefix,
                                       std::string* name);
bool ParseRenderedTableLiteralStartLine(std::string_view line,
                                        std::string* indent,
                                        std::string* lhs_prefix,
                                        std::string* name);
bool ParseRenderedTableFieldAssignment(std::string_view line,
                                       std::string_view table_name,
                                       std::string* field_key,
                                       std::string* rhs_expression,
                                       bool* rhs_is_function,
                                       int* array_index);
bool ParseRenderedAssignmentLine(std::string_view line,
                                 bool* is_local,
                                 std::vector<std::string>* lhs_names,
                                 std::string* rhs_expression);
bool RemoveRenderedLuaEmptyIfBlocks(std::vector<std::string>* lines);
bool RemoveRenderedLuaNoOpAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedTableInitializers(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDefaultNilAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNilLocalInitializers(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateNilResets(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImpossibleAsserts(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNamedNilAsserts(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBooleanAssertConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateAssignedAssertReuses(std::vector<std::string>* lines);
bool SimplifyRenderedLuaEmptyIfAssertGuards(std::vector<std::string>* lines);
bool SimplifyRenderedLuaEmptyGuardedNilAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaEmptyConditionBooleanReturns(std::vector<std::string>* lines);
bool SimplifyRenderedLuaConditionalReturns(std::vector<std::string>* lines);
bool ExpandRenderedLuaPrepareToRunBooleanReturns(std::vector<std::string>* lines);
bool ExpandRenderedLuaPredicateBooleanReturnsForParity(std::vector<std::string>* lines);
bool ExpandRenderedLuaNegatedBooleanReturnsForParity(std::vector<std::string>* lines);
bool SimplifyRenderedLuaStateElseAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaConditionalFallbackAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSelfReferentialFallbackAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRedundantConditionLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBranchLocalAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBranchShadowedLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedSourceLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedSelfAssignmentSetters(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSelfOrAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNilSeededSelfFallbackAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSeededShadowedLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedDefaultLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedSelectionLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedGuardedSelectionLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedMemberSelectionFallbackAssignments(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedTruthySelectionLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedMemberNonemptySelections(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNegatedSelfFallbackAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBooleanCoercionSelections(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBooleanCoercionReturns(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedCollectionAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUndeclaredCollectionLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaEmptyGuardedSeedAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaEmptyGuardedCarryAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedLoopFallbackAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTemporaryMultiResultCopies(std::vector<std::string>* lines);
bool SimplifyRenderedLuaCallResultFallbackAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedCallFallbackAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedTemporaryFallbackAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedBranchLocalFallbackAssignments(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedGuardedLocalFallbackAssignments(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedTruthyLiteralFallbackAssignments(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaEmptyNilGuardLiteralAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateCallLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateShadowedLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRedundantLocalRedeclarations(std::vector<std::string>* lines);
bool SimplifyRenderedLuaIfBranchShadowLocalCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateIdentifierCarryLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateExpressionCarryLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateMemberCarryLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaClosureShadowLocalAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaInvalidEmptyStringMethodReceivers(std::vector<std::string>* lines);
bool SimplifyRenderedLuaAssignedFunctionDeclarations(std::vector<std::string>* lines);
bool SimplifyRenderedLuaAdjacentDuplicateLocalCallDeclarations(std::vector<std::string>* lines);
bool SimplifyRenderedLuaQuestManagerCameraHelperDefaults(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateBooleanAliasCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNearbyBooleanAliasLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaEscapingBooleanAliasUses(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTemporaryIdentifierCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaStraightLineSingleUseTemps(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBinaryMethodCallCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaConditionBinaryMethodCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaCallArgumentBinaryMethodCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaExplicitSelfMethodArguments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRedundantColonSelfArguments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaMethodArg0SelfParameters(std::vector<std::string>* lines);
bool UnwrapRenderedLuaReturnedCallsForExactParity(std::vector<std::string>* lines);
bool WrapRenderedLuaReturnedCallsForExactParity(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTopLevelNamespaceTableAliasChains(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedCallArgumentFallbackLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaLocalCallBootstrapGuards(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSelfCallShadowLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDeclaredMultiResultCallLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateVarargLoopTables(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateTemporaryAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateTemporaryCallArgumentUses(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateTemporaryStatementUses(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSwapAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSimpleGuardedDefaultAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedDefaultAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBooleanLoadPairs(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBooleanNumericLocals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBooleanCarryConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSeededBooleanTrueConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDependentAssignedRepeatedConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateAssignedRepeatedConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNearbyAssignedRepeatedConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNearbyAssignedStatementChainUses(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRedundantGuardConditionParentheses(
    std::vector<std::string>* lines);
bool HoistRenderedLuaSingleWhileElseTailsForExactParity(std::vector<std::string>* lines);
bool CollapseRenderedLuaNestedSharedElseGuardsForExactParity(
    std::vector<std::string>* lines);
bool CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity(
    std::vector<std::string>* lines);
bool RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateAssignedConditionExpressionUses(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateSingleUseGuardTemps(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateRedundantConditionTemps(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateAssignedCallGuards(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateAssignedMethodCallGuards(std::vector<std::string>* lines);
bool SimplifyRenderedLuaAdjacentDuplicateCallValueConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateLocalCallConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateLocalCallFieldConditions(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRepeatedLocalCallPropertyUsesForParity(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaRepeatedLocalCallExpressionUsesForParity(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaRepeatedLocalExpressionUsesForParity(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaRepeatedMethodExpressionConditionsForParity(
    std::vector<std::string>* lines);
bool ExpandRenderedLuaPostCallLocalAliasesForParity(std::vector<std::string>* lines);
bool LocalizeRenderedLuaGroupMindAttachedGuardCallsForParity(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnusedLocalTableLiteralAssignmentsForParity(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaCallReassignmentSelfCompareGuards(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateAssignedRepeatedCallLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedAssignedGuardChains(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedTrueFlagSearchElse(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedGuardedConditionChains(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledStateIfComments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledIfCommentBranchChains(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledIfCommentLeadingSuffixes(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledIfCommentNestedTailChains(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledIfCommentNilGuardSplits(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledSparseNilGuardBranches(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledOuterOrNestedElseIfChains(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledFlaggedElseIfChains(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledStateYieldCommentLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledCurrentStateLeadingGuards(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledNestedCurrentStateElseIfBranches(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledNestedCurrentStateGuardBranches(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedElseIfChainsGeneric(std::vector<std::string>* lines);
bool SimplifyRenderedLuaUnhandledRetargetBreakYieldLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaMessagePollingLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaPrecheckedGuardedWaitLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaLoopGuardedYieldElse(std::vector<std::string>* lines);
bool SimplifyRenderedLuaYieldElseWaitLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDanglingOuterTrueWaitWrappers(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDanglingElseGuardedWaitLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaIndexedAliveCleanupLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRepeatedCallGuards(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRepeatedCallFieldGuardsForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaMeterPercentLocalForParity(std::vector<std::string>* lines);
bool SimplifyRenderedLuaScopedRepeatedCallComparisonGuardsForParity(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaTimingRelationalCallOrderForParity(
    std::vector<std::string>* lines);
bool ExpandRenderedLuaAndNotElseGuardsForParity(std::vector<std::string>* lines);
bool SimplifyRenderedLuaConditionalCallFallbacks(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDanglingDestroyReplayCalls(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedGuardedDuplicateBodies(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedGuardedFallbackDuplicateBodies(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedGuardedAssignmentFallbackElse(std::vector<std::string>* lines);
bool SimplifyRenderedLuaEmptyElseBranches(std::vector<std::string>* lines);
bool NormalizeRenderedLuaDebugDrawTextZOffsetForParity(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateMissingElseAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateMissingElseTrailingTableCalls(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedDuplicateElseAssignments(std::vector<std::string>* lines);
bool ExpandRenderedLuaTerminalIfActionEmptyElseForParity(std::vector<std::string>* lines);
bool CollapseRenderedLuaNestedDuplicateReturnGuardsForParity(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDuplicateIfElseReturnsForParity(std::vector<std::string>* lines);
bool SimplifyRenderedLuaPostIfFallbackElse(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSignedMagnitudeClampBranches(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTrailingReturnFallbackElse(std::vector<std::string>* lines);
bool SimplifyRenderedLuaLoopTrailingReturnElse(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedMissingElseDuplicateTails(std::vector<std::string>* lines);
bool SimplifyRenderedLuaMissingElseDuplicateTails(std::vector<std::string>* lines);
bool ExpandRenderedLuaReturnThenFallbackElseForParity(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTableLiteralCapturedTargetAliases(std::vector<std::string>* lines);
bool RestoreRenderedLuaTopLevelSubclassBindings(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTopLevelMemberAliases(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDeadTemporaryAliasAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaScopedSequentialMemberCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaScopedMemberAliases(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTopLevelImmediateResultAliases(std::vector<std::string>* lines);
bool SimplifyRenderedLuaLocalTableBuilderCallAliases(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTopLevelFunctionAliasAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTopLevelTableLiteralAliasBuilders(std::vector<std::string>* lines);
bool ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity(
    std::vector<std::string>* lines);
bool ExpandRenderedLuaOrTableFallbackAssignmentsForParity(
    std::vector<std::string>* lines);
bool ExpandRenderedLuaInlineLocalTableSeedFieldsForParity(
    std::vector<std::string>* lines);
bool SimplifyRenderedLuaDanglingTopLevelIndexedAliases(std::vector<std::string>* lines);
std::size_t FindRenderedTableLiteralEnd(const std::vector<std::string>& lines,
                                        std::size_t start_line);
bool LocalizeRenderedLuaFunctionLocals(std::vector<std::string>* lines);
bool RewriteTrailingRenderedTableBuilderTarget(std::vector<std::string>* lines,
                                               std::string_view table_name,
                                               std::string_view target_expression);
bool RestoreRenderedLuaBaseObjectInitializers(std::vector<std::string>* lines);
bool NormalizeRenderedLuaNilTablePlaceholders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRedundantIpairsTableReseeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaLoopActionBuilderAliases(std::vector<std::string>* lines);
bool NormalizeRenderedLuaMixedTableBuilderAliases(std::vector<std::string>* lines);
bool NormalizeRenderedLuaForwardTableBuilderAliases(std::vector<std::string>* lines);
bool SimplifyRenderedLuaOrphanedTopLevelMemberBuilderAliases(std::vector<std::string>* lines);
bool NormalizeRenderedLuaFreshTargetTableSeeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaStaleTargetTableAliases(std::vector<std::string>* lines);
bool SimplifyRenderedLuaStaleTargetLiteralOverwrites(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDelayedArrayTableBuilders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSplitGenericForIteratorLoops(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaSubclassBootstrapSetlistChains(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTopLevelObjectCarryAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaKnownFieldCarryAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaImmediateLocalLiteralSeeds(std::vector<std::string>* lines);
bool SimplifyRenderedLuaForwardLocalLiteralSeeds(std::vector<std::string>* lines);
bool SimplifyRenderedLuaForwardCallArgumentLiteralSeeds(std::vector<std::string>* lines);
bool SimplifyRenderedLuaForwardInvalidTemporaryTableSeeds(std::vector<std::string>* lines);
bool SimplifyRenderedLuaForwardSetlistPairSeeds(std::vector<std::string>* lines);
bool SimplifyRenderedLuaBalverineSequenceSetlistArtifacts(std::vector<std::string>* lines);
bool SimplifyRenderedLuaPendingRegisterMethodResultCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedLiteralIndexAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTempKeyedTableLiteralAliases(std::vector<std::string>* lines);
bool ExpandRenderedLuaSingletonTopLevelTableLiterals(std::vector<std::string>* lines);
bool ExpandRenderedLuaQualifiedTablePlaceholderLiterals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaQualifiedTableLiteralArrayTails(std::vector<std::string>* lines);
bool SimplifyRenderedLuaQualifiedTableBuilders(std::vector<std::string>* lines);
bool ExpandRenderedLuaLargeQualifiedTableLiterals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTableBuilders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaLocalTableBuilderCallArguments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaNestedTableLiteralPlaceholders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaReturnedTableBuilders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTableInitializerTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTableLiteralTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaEscapingGuardedLocalDeclarations(std::vector<std::string>* lines);
bool SimplifyRenderedLuaRepeatedCallResultGuards(std::vector<std::string>* lines);
bool SimplifyRenderedLuaClosureCarryShadowAssignments(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGuardedNilFallbackReturns(std::vector<std::string>* lines);
bool SimplifyRenderedLuaAssignedFunctionTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaFunctionTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaConditionalIifes(std::vector<std::string>* lines);
bool SimplifyRenderedLuaAssertConditionBlocks(std::vector<std::string>* lines);
bool SimplifyRenderedLuaConditionHeaders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaIntegralFloatLiterals(std::vector<std::string>* lines);
bool SimplifyRenderedLuaExpressionParentheses(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDotAccess(std::vector<std::string>* lines);
bool SimplifyRenderedLuaCallParentheses(std::vector<std::string>* lines);
bool ApplyRenderedLuaQuestStructuralCleanupProfile(std::vector<std::string>* lines);
bool ApplyRenderedLuaBootstrapCleanupProfile(std::vector<std::string>* lines);
bool ExpandRenderedLuaTerminalEmptyElseIfChainsForParity(std::vector<std::string>* lines);
bool ApplyRenderedLuaExactParityNormalizationProfile(std::vector<std::string>* lines);
bool TrySwapRenderedCallRightHandComparisonsForParity(std::vector<std::string>* lines);
bool TrySwapRenderedLeadingCallComparisonsForExactParity(std::vector<std::string>* lines);
bool TrySwapRenderedStableRelationalComparisonsForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaLoopBreakElseToPostIfForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaPostGuardDebugErrorElseForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaNearExactHeadGlobalSeeds(std::vector<std::string>* lines);
bool ComposeRenderedLuaValidatedMemberAliasCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaQualifiedTableBuilderCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaOrderedCompareSelfCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaConditionTempGuardCleanupTailForNearExact(
    std::vector<std::string>* lines);
bool ComposeRenderedLuaConditionTempGuardCleanupTailForValidated(
    std::vector<std::string>* lines);
bool ComposeRenderedLuaSmallCallCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaAssignedGuardCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaMemberAliasCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaNamespaceExportAliasCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaTopLevelAliasCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaPredicateReturnParityCleanupTail(std::vector<std::string>* lines);
bool ComposeRenderedLuaNearExactCommitParityCleanupTail(std::vector<std::string>* lines);
bool SimplifyRenderedLuaInvalidAnonymousCallArtifacts(std::vector<std::string>* lines);
bool SimplifyRenderedLuaGamefaceFunctionPlaceholders(std::vector<std::string>* lines);
bool DisambiguateRenderedLuaLeadingIifeStatements(std::vector<std::string>* lines);
bool NormalizeRenderedLuaWhitespace(std::vector<std::string>* lines);

struct RenderedLuaCleanupProfileTelemetry {
    std::string profile_name;
    std::string hottest_step;
    std::string capped_steps;
    std::uint64_t total_microseconds = 0;
    std::uint64_t hottest_step_microseconds = 0;
    int step_count = 0;
    int changed_steps = 0;
    int capped_step_count = 0;
};

const RenderedLuaCleanupProfileTelemetry& GetLastRenderedLuaCleanupProfileTelemetry();
void ResetRenderedLuaCleanupProfileTelemetry();

void AppendIndentedStatement(std::vector<std::string>* lines,
                             std::string_view indent,
                             std::string_view statement);

}  // namespace refii::lua
