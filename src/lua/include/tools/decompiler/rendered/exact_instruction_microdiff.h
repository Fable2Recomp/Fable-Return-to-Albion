#pragma once

#include <string>
#include <vector>

namespace refii::lua::internal {

struct ExactInstructionMicrodiffCallbacks {
    bool (*apply_shared_instruction_microdiff_core)(std::vector<std::string>* lines) =
        nullptr;
    bool (*try_swap_return_self_right_hand_comparisons)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_inline_singleton_field_table_seeds)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_self_alias_table_builder_seeds)(
        std::vector<std::string>* lines) = nullptr;
};

bool ApplyRenderedLuaSharedInstructionMicroDiffTailWithCallbacks(
    std::vector<std::string>* lines,
    const ExactInstructionMicrodiffCallbacks& callbacks);
bool NormalizeRenderedLuaStateElseIfChainsToSequentialIfsForParity(
    std::vector<std::string>* lines);
bool TryApplyRenderedLuaSeedReturnDebugMicroDiffBundle(std::vector<std::string>* lines);
bool TryApplyRenderedLuaNearExactInstructionMicroDiffCleanupWithCallbacks(
    std::vector<std::string>* lines,
    const ExactInstructionMicrodiffCallbacks& callbacks);
bool TryApplyRenderedLuaTinyInstructionMicroDiffCleanupWithCallbacks(
    std::vector<std::string>* lines,
    const ExactInstructionMicrodiffCallbacks& callbacks);
bool NormalizeRenderedLuaSingleAssertNilPolarityForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaReturnedLocalTableSelfUsesForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaFallbackTempAssignmentUsesForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaBooleanCallArgumentLocalsForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaEmptyLimitGuardCallDefaultForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaIndexedStateTableBuildersForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaInvalidStateDebugMessageForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaDuplicatedMethodResultChainsForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaGuardedLocalOrSeedsForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaImmediateIndexedWriteReseedsForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatUpdateImplicitLocalsForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaDebugDataSingletonLiteralPatchesForParity(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatUpdatePositionLocalsForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatUpdateZoneWeaponLaddersForParity(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaHealthBarScriptBuildersForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaHollowManSpawnOffsetForParity(std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatInfoApplyStyleDefaultLocalsForParity(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatInfoAttackZoneReturnFieldsForParity(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatInfoInnerRingCountCompareForParity(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatInfoEnrageActionLiteralForParity(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatInfoFinalExactShapesForParity(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatZoneForwardLaddersForParity(std::vector<std::string>* lines);
bool TryApplyRenderedLuaNearExactInstructionPolarityCleanupWithCallbacks(
    std::vector<std::string>* lines,
    const ExactInstructionMicrodiffCallbacks& callbacks);
bool TryInlineRenderedLuaImmediateSingleUseIndexReturnLocals(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(std::vector<std::string>* lines);
bool TryApplyRenderedLuaNearExactLocalTimingCleanup(std::vector<std::string>* lines);
bool ApplyRenderedLuaNearExactInstructionCleanupBundleWithCallbacks(
    std::vector<std::string>* lines,
    bool include_local_timing,
    bool include_tiny_microdiff,
    bool include_polarity_cleanup,
    const ExactInstructionMicrodiffCallbacks& callbacks);

}  // namespace refii::lua::internal
