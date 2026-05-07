#pragma once

#include "tools/decompiler/rendered/shared_cleanup_rules.h"
#include "tools/decompiler/rendered/validated_cleanup_pipeline.h"

#include <string>
#include <vector>

namespace refii::lua::internal {

struct SharedCleanupRuleTableCallbacks {
    bool (*normalize_local_literal_missing_field_seeds)(std::vector<std::string>* lines) =
        nullptr;
    bool (*simplify_redundant_table_reseeds_before_field_writes)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_near_exact_singleton_index_table_assignments)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_seeded_singleton_table_replacement_chains)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_hashed_key_load_table_assignments)(std::vector<std::string>* lines) =
        nullptr;
    bool (*normalize_sparse_singleton_table_replacement_chains)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_chained_table_element_assignments)(std::vector<std::string>* lines) =
        nullptr;
    bool (*normalize_near_exact_singleton_field_table_assignments)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_near_exact_top_level_singleton_field_seeds)(
        std::vector<std::string>* lines) = nullptr;
    bool (*normalize_quest_carrier_placeholder_cleanup)(std::vector<std::string>* lines) =
        nullptr;
};

void ApplySharedCleanupRuleTableSteps(
    RenderedLuaCleanupProfile profile,
    std::vector<std::string>* lines,
    const SharedCleanupRuleTableCallbacks& callbacks,
    const RenderedCleanupRuleVisitor& apply_rule);

}  // namespace refii::lua::internal
