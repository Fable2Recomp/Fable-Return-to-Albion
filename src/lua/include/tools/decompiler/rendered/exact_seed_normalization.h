#pragma once

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

enum class RenderedLuaSeedNormalizationLevel {
    kHazard,
    kStandard,
    kExtended,
};

struct ExactSeedNormalizationCallbacks {
    bool (*normalize_create_subclass_seeds)(std::vector<std::string>* lines) = nullptr;
    bool (*normalize_create_subclass_seeds_fallback)(std::vector<std::string>* lines) =
        nullptr;
    bool (*normalize_singleton_field_table_assignments)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_singleton_index_table_assignments)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_seeded_singleton_table_replacement_chains)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_hashed_key_load_table_assignments)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_sparse_singleton_table_replacement_chains)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_chained_table_element_assignments)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_top_level_singleton_field_seeds)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_missing_field_base_seeds)(std::vector<std::string>* lines) =
        nullptr;
    bool (*normalize_forward_field_writes_before_local_table_seeds)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_missing_indexed_table_seeds)(std::vector<std::string>* lines) =
        nullptr;
};

bool NormalizeRenderedLuaMissingDottedFieldSeeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatStylesIndexedAliasWrites(std::vector<std::string>* lines);
bool NormalizeRenderedLuaFieldNilTablePlaceholdersBeforeWrites(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaLiteralNilFieldPlaceholdersBeforeWrites(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaCallFieldTableAliasSeeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaLocalLiteralMissingFieldSeeds(std::vector<std::string>* lines);
std::string NormalizeRenderedSeedBundleExpression(std::string_view expression);
bool IsRenderedSeedBundleSafePrefixExpression(std::string_view expression);
bool ParseRenderedSeedBundleSingleAssignment(std::string_view line,
                                             bool* is_local_out,
                                             std::string* lhs_out,
                                             std::string* rhs_out);
bool NormalizeRenderedLuaTemporaryIndexedFieldSeeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaDottedIndexedChildTableSeeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaTemporaryNestedFieldSeeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaCombatStylesNestedFieldSeeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaPermanentsTableHelperGuards(std::vector<std::string>* lines);
bool NormalizeRenderedLuaSpecialChestGlobalSeed(std::vector<std::string>* lines);
bool IsRenderedRegisterTemporary(std::string_view token);
std::string RenderedLeadingIdentifier(std::string_view expression);
bool NormalizeRenderedLuaUndefinedTemporaryTableSeeds(std::vector<std::string>* lines);
bool NormalizeRenderedLuaUndefinedTemporaryAliasAssignments(std::vector<std::string>* lines);
bool NormalizeRenderedLuaForwardOwnerAliasSeedsBeforeFieldWrites(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaConsecutiveTemporaryTableLiteralOverwrites(
    std::vector<std::string>* lines);
bool NormalizeRenderedLuaBareCreateSubClassCallsBeforeMemberWrites(
    std::vector<std::string>* lines);
std::size_t CountRenderedLuaRuntimeSeedHazards(std::string_view source);
bool ApplyRenderedLuaSeedNormalizationsWithCallbacks(
    std::vector<std::string>* lines,
    RenderedLuaSeedNormalizationLevel level,
    const ExactSeedNormalizationCallbacks& callbacks);
bool ApplyRenderedLuaNearExactSingletonSeedClusterWithCallbacks(
    std::vector<std::string>* lines,
    bool include_subclass_fallback,
    bool include_local_literal_missing_field,
    const ExactSeedNormalizationCallbacks& callbacks);
bool ApplyRenderedLuaSharedInstructionMicroDiffCoreWithCallbacks(
    std::vector<std::string>* lines,
    const ExactSeedNormalizationCallbacks& callbacks);

}  // namespace refii::lua::internal
