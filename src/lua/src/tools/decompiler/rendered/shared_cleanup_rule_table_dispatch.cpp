#include "tools/decompiler/rendered/shared_cleanup_rule_table_dispatch.h"

#include "tools/decompiler/rendered/shared_cleanup_signal_helpers.h"
#include "tools/decompiler/rendered/shared_guard_cleanup_repairs.h"
#include "tools/decompiler/rendered/shared_table_cleanup_repairs.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <array>
#include <utility>

namespace refii::lua::internal {

namespace {

thread_local const SharedCleanupRuleTableCallbacks* g_active_callbacks = nullptr;

bool ApplyNormalizeLocalLiteralMissingFieldSeeds(std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_local_literal_missing_field_seeds != nullptr &&
           g_active_callbacks->normalize_local_literal_missing_field_seeds(lines);
}

bool ApplySimplifyRedundantTableReseedsBeforeFieldWrites(
    std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->simplify_redundant_table_reseeds_before_field_writes !=
               nullptr &&
           g_active_callbacks->simplify_redundant_table_reseeds_before_field_writes(lines);
}

bool ApplyNormalizeNearExactSingletonIndexTableAssignments(
    std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_near_exact_singleton_index_table_assignments !=
               nullptr &&
           g_active_callbacks->normalize_near_exact_singleton_index_table_assignments(lines);
}

bool ApplyNormalizeSeededSingletonTableReplacementChains(
    std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_seeded_singleton_table_replacement_chains !=
               nullptr &&
           g_active_callbacks->normalize_seeded_singleton_table_replacement_chains(lines);
}

bool ApplyNormalizeHashedKeyLoadTableAssignments(std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_hashed_key_load_table_assignments != nullptr &&
           g_active_callbacks->normalize_hashed_key_load_table_assignments(lines);
}

bool ApplyNormalizeSparseSingletonTableReplacementChains(
    std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_sparse_singleton_table_replacement_chains !=
               nullptr &&
           g_active_callbacks->normalize_sparse_singleton_table_replacement_chains(lines);
}

bool ApplyNormalizeChainedTableElementAssignments(std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_chained_table_element_assignments != nullptr &&
           g_active_callbacks->normalize_chained_table_element_assignments(lines);
}

bool ApplyNormalizeNearExactSingletonFieldTableAssignments(
    std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_near_exact_singleton_field_table_assignments !=
               nullptr &&
           g_active_callbacks->normalize_near_exact_singleton_field_table_assignments(lines);
}

bool ApplyNormalizeNearExactTopLevelSingletonFieldSeeds(
    std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_near_exact_top_level_singleton_field_seeds !=
               nullptr &&
           g_active_callbacks->normalize_near_exact_top_level_singleton_field_seeds(lines);
}

bool ApplyNormalizeQuestCarrierPlaceholderCleanup(std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->normalize_quest_carrier_placeholder_cleanup != nullptr &&
           g_active_callbacks->normalize_quest_carrier_placeholder_cleanup(lines);
}


constexpr std::array<RenderedCleanupRuleDescriptor, 12> kAggressivePreludeRules = {{
    {"guarded_selection_locals",
     RenderedCleanupRuleFamily::kGuard,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaGuardedSelectionLocals},
    {"nested_guarded_selection_locals",
     RenderedCleanupRuleFamily::kGuard,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaNestedGuardedSelectionLocals},
    {"guarded_member_selection_fallback",
     RenderedCleanupRuleFamily::kGuard,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaGuardedMemberSelectionFallbackAssignments},
    {"guarded_truthy_selection_locals",
     RenderedCleanupRuleFamily::kGuard,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaGuardedTruthySelectionLocals},
    {"nested_guarded_assignment_fallback_else",
     RenderedCleanupRuleFamily::kGuard,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaNestedGuardedAssignmentFallbackElse},
    {"binary_method_call_carries",
     RenderedCleanupRuleFamily::kCall,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaBinaryMethodCallCarries},
    {"condition_binary_method_carries",
     RenderedCleanupRuleFamily::kCall,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaConditionBinaryMethodCarries},
    {"call_argument_binary_method_carries",
     RenderedCleanupRuleFamily::kCall,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaCallArgumentBinaryMethodCarries},
    {"guarded_call_argument_fallback_locals",
     RenderedCleanupRuleFamily::kCall,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaGuardedCallArgumentFallbackLocals},
    {"declared_multi_result_call_locals",
     RenderedCleanupRuleFamily::kCall,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaDeclaredMultiResultCallLocals},
    {"seeded_boolean_true_conditions",
     RenderedCleanupRuleFamily::kCondition,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaSeededBooleanTrueConditions},
    {"immediate_assigned_repeated_conditions",
     RenderedCleanupRuleFamily::kCondition,
     RenderedCleanupRuleScope::kAggressiveOnly,
     nullptr,
     refii::lua::SimplifyRenderedLuaImmediateAssignedRepeatedConditions},
}};

#define REFII_SHARED_RENDERED_CLEANUP_RULES(X)                                                \
    X("repair_empty_nil_guarded_assignments", RenderedCleanupRuleFamily::kGuard,              \
      RenderedCleanupRuleScope::kBoth, nullptr, TryRepairEmptyNilGuardedAssignments)          \
    X("repair_table_builder_assignment_carries", RenderedCleanupRuleFamily::kTable,           \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                           \
      TryRepairTableBuilderAssignmentCarries)                                                  \
    X("quest_structural_cleanup", RenderedCleanupRuleFamily::kQuest,                          \
      RenderedCleanupRuleScope::kBoth, HasQuestLikeCleanupSignals,                            \
      ApplyQuestStructuralCleanupIfRelevant)                                                   \
    X("restore_top_level_subclass_bindings", RenderedCleanupRuleFamily::kAlias,               \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::RestoreRenderedLuaTopLevelSubclassBindings)                                  \
    X("subclass_bootstrap_setlist_chains", RenderedCleanupRuleFamily::kTable,                 \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaSubclassBootstrapSetlistChains)                           \
    X("scoped_sequential_member_carries", RenderedCleanupRuleFamily::kAlias,                  \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries)                            \
    X("scoped_member_aliases", RenderedCleanupRuleFamily::kAlias,                              \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaScopedMemberAliases) \
    X("top_level_member_aliases", RenderedCleanupRuleFamily::kAlias,                          \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaTopLevelMemberAliases) \
    X("dead_temporary_alias_assignments", RenderedCleanupRuleFamily::kAlias,                  \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaDeadTemporaryAliasAssignments)                            \
    X("immediate_temporary_assignments", RenderedCleanupRuleFamily::kLocal,                   \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments) \
    X("immediate_temporary_call_argument_uses", RenderedCleanupRuleFamily::kLocal,            \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateTemporaryCallArgumentUses)                       \
    X("invalid_empty_string_method_receivers", RenderedCleanupRuleFamily::kCall,              \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaInvalidEmptyStringMethodReceivers)                        \
    X("assigned_function_declarations", RenderedCleanupRuleFamily::kMisc,                     \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaAssignedFunctionDeclarations) \
    X("quest_manager_camera_defaults", RenderedCleanupRuleFamily::kMisc,                      \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaQuestManagerCameraHelperDefaults)                         \
    X("top_level_immediate_result_aliases", RenderedCleanupRuleFamily::kAlias,                \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaTopLevelImmediateResultAliases)                           \
    X("local_table_builder_call_aliases", RenderedCleanupRuleFamily::kTable,                  \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaLocalTableBuilderCallAliases)                             \
    X("top_level_function_alias_assignments", RenderedCleanupRuleFamily::kAlias,              \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaTopLevelFunctionAliasAssignments)                         \
    X("top_level_table_literal_alias_builders", RenderedCleanupRuleFamily::kTable,            \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaTopLevelTableLiteralAliasBuilders)                        \
    X("dangling_top_level_indexed_aliases", RenderedCleanupRuleFamily::kAlias,                \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaDanglingTopLevelIndexedAliases)                           \
    X("table_literal_captured_target_aliases", RenderedCleanupRuleFamily::kTable,             \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaTableLiteralCapturedTargetAliases)                        \
    X("qualified_table_builder_cleanup_tail", RenderedCleanupRuleFamily::kTable,              \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyQualifiedTableBuilderCleanupIfRelevant)                                              \
    X("normalize_mixed_table_builder_aliases", RenderedCleanupRuleFamily::kTable,             \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::NormalizeRenderedLuaMixedTableBuilderAliases)                                \
    X("normalize_forward_table_builder_aliases", RenderedCleanupRuleFamily::kTable,           \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::NormalizeRenderedLuaForwardTableBuilderAliases)                              \
    X("orphaned_top_level_member_builder_aliases", RenderedCleanupRuleFamily::kTable,         \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaOrphanedTopLevelMemberBuilderAliases)                     \
    X("normalize_fresh_target_table_seeds", RenderedCleanupRuleFamily::kTable,                \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::NormalizeRenderedLuaFreshTargetTableSeeds)                                   \
    X("normalize_local_literal_missing_field_seeds_a", RenderedCleanupRuleFamily::kTable,     \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeLocalLiteralMissingFieldSeeds)                                        \
    X("top_level_temporary_multi_table_assignments", RenderedCleanupRuleFamily::kTable,       \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments)                   \
    X("split_generic_for_iterator_loops", RenderedCleanupRuleFamily::kLoop,                   \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaSplitGenericForIteratorLoops) \
    X("setlist_collapsed_top_level_assignments", RenderedCleanupRuleFamily::kTable,           \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments)                      \
    X("redundant_ipairs_table_reseeds", RenderedCleanupRuleFamily::kTable,                    \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaRedundantIpairsTableReseeds)                              \
    X("redundant_table_reseeds_before_field_writes", RenderedCleanupRuleFamily::kTable,       \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplySimplifyRedundantTableReseedsBeforeFieldWrites)                               \
    X("near_exact_singleton_index_table_assignments", RenderedCleanupRuleFamily::kTable,      \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeNearExactSingletonIndexTableAssignments)                             \
    X("seeded_singleton_table_replacement_chains", RenderedCleanupRuleFamily::kTable,         \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeSeededSingletonTableReplacementChains)                               \
    X("hashed_key_load_table_assignments", RenderedCleanupRuleFamily::kTable,                 \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeHashedKeyLoadTableAssignments)                                        \
    X("sparse_singleton_table_replacement_chains", RenderedCleanupRuleFamily::kTable,         \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeSparseSingletonTableReplacementChains)                               \
    X("chained_table_element_assignments", RenderedCleanupRuleFamily::kTable,                 \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeChainedTableElementAssignments)                                       \
    X("near_exact_singleton_field_table_assignments", RenderedCleanupRuleFamily::kTable,      \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeNearExactSingletonFieldTableAssignments)                             \
    X("near_exact_top_level_singleton_field_seeds", RenderedCleanupRuleFamily::kTable,        \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeNearExactTopLevelSingletonFieldSeeds)                                \
    X("normalize_local_literal_missing_field_seeds_b", RenderedCleanupRuleFamily::kTable,     \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      ApplyNormalizeLocalLiteralMissingFieldSeeds)                                        \
    X("top_level_object_carry_assignments", RenderedCleanupRuleFamily::kAlias,                \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaTopLevelObjectCarryAssignments)                           \
    X("known_field_carry_assignments", RenderedCleanupRuleFamily::kAlias,                     \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaKnownFieldCarryAssignments) \
    X("immediate_local_literal_seeds", RenderedCleanupRuleFamily::kTable,                     \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaImmediateLocalLiteralSeeds)                               \
    X("forward_local_literal_seeds", RenderedCleanupRuleFamily::kTable,                       \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaForwardLocalLiteralSeeds)                                 \
    X("forward_call_argument_literal_seeds", RenderedCleanupRuleFamily::kTable,               \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaForwardCallArgumentLiteralSeeds)                          \
    X("forward_invalid_temporary_table_seeds", RenderedCleanupRuleFamily::kTable,             \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaForwardInvalidTemporaryTableSeeds)                        \
    X("forward_setlist_pair_seeds", RenderedCleanupRuleFamily::kTable,                        \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaForwardSetlistPairSeeds)                                  \
    X("quest_carrier_placeholder_cleanup", RenderedCleanupRuleFamily::kCall,                  \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      ApplyNormalizeQuestCarrierPlaceholderCleanup)                                      \
    X("balverine_sequence_setlist_artifacts", RenderedCleanupRuleFamily::kTable,              \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaBalverineSequenceSetlistArtifacts)                        \
    X("pending_register_method_result_carries", RenderedCleanupRuleFamily::kCall,             \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaPendingRegisterMethodResultCarries)                       \
    X("nested_literal_index_assignments", RenderedCleanupRuleFamily::kTable,                  \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaNestedLiteralIndexAssignments)                            \
    X("temp_keyed_table_literal_aliases", RenderedCleanupRuleFamily::kTable,                  \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaTempKeyedTableLiteralAliases)                             \
    X("table_builders", RenderedCleanupRuleFamily::kTable,                                     \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaTableBuilders)                                             \
    X("local_table_builder_call_arguments", RenderedCleanupRuleFamily::kTable,                \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaLocalTableBuilderCallArguments)                           \
    X("local_table_literal_call_assignments", RenderedCleanupRuleFamily::kTable,              \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaLocalTableLiteralCallAssignments)                         \
    X("immediate_temporary_statement_uses", RenderedCleanupRuleFamily::kLocal,                \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateTemporaryStatementUses)                          \
    X("condition_headers", RenderedCleanupRuleFamily::kCondition,                              \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaConditionHeaders) \
    X("redundant_guard_condition_parentheses", RenderedCleanupRuleFamily::kCondition,         \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaRedundantGuardConditionParentheses)                       \
    X("immediate_assigned_condition_expression_uses", RenderedCleanupRuleFamily::kCondition,  \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateAssignedConditionExpressionUses)                 \
    X("immediate_assigned_call_guards", RenderedCleanupRuleFamily::kCall,                     \
      RenderedCleanupRuleScope::kAggressiveOnly, nullptr,                                      \
      refii::lua::SimplifyRenderedLuaImmediateAssignedCallGuards)                              \
    X("immediate_assigned_method_call_guards", RenderedCleanupRuleFamily::kCall,              \
      RenderedCleanupRuleScope::kAggressiveOnly, nullptr,                                      \
      refii::lua::SimplifyRenderedLuaImmediateAssignedMethodCallGuards)                        \
    X("immediate_local_call_conditions", RenderedCleanupRuleFamily::kCall,                    \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateLocalCallConditions)                             \
    X("immediate_assigned_repeated_call_loops", RenderedCleanupRuleFamily::kLoop,             \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateAssignedRepeatedCallLoops)                       \
    X("repeated_call_guards", RenderedCleanupRuleFamily::kCall,                                \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaRepeatedCallGuards) \
    X("adjacent_duplicate_call_value_conditions", RenderedCleanupRuleFamily::kCondition,      \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaAdjacentDuplicateCallValueConditions)                     \
    X("nearby_assigned_statement_chain_uses", RenderedCleanupRuleFamily::kLocal,              \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaNearbyAssignedStatementChainUses)                         \
    X("nested_table_literal_placeholders", RenderedCleanupRuleFamily::kTable,                 \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaNestedTableLiteralPlaceholders)                           \
    X("returned_table_builders", RenderedCleanupRuleFamily::kTable,                            \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaReturnedTableBuilders)                                    \
    X("table_initializer_temporaries", RenderedCleanupRuleFamily::kTable,                     \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaTableInitializerTemporaries)                              \
    X("table_literal_temporaries", RenderedCleanupRuleFamily::kTable,                          \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaTableLiteralTemporaries)                                  \
    X("normalize_stale_target_table_aliases", RenderedCleanupRuleFamily::kTable,              \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::NormalizeRenderedLuaStaleTargetTableAliases)                                 \
    X("stale_target_literal_overwrites", RenderedCleanupRuleFamily::kTable,                   \
      RenderedCleanupRuleScope::kBoth, HasTableAliasCleanupSignals,                            \
      refii::lua::SimplifyRenderedLuaStaleTargetLiteralOverwrites)                             \
    X("escaping_guarded_local_declarations", RenderedCleanupRuleFamily::kGuard,               \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaEscapingGuardedLocalDeclarations)                         \
    X("repeated_call_result_guards", RenderedCleanupRuleFamily::kCall,                         \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaRepeatedCallResultGuards) \
    X("closure_carry_shadow_assignments", RenderedCleanupRuleFamily::kLocal,                  \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaClosureCarryShadowAssignments)                            \
    X("guarded_nil_fallback_returns", RenderedCleanupRuleFamily::kReturn,                     \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaGuardedNilFallbackReturns)                                \
    X("local_call_bootstrap_guards", RenderedCleanupRuleFamily::kCall,                        \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaLocalCallBootstrapGuards)                                 \
    X("self_call_shadow_locals", RenderedCleanupRuleFamily::kCall,                             \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaSelfCallShadowLocals) \
    X("empty_if_assert_guards", RenderedCleanupRuleFamily::kGuard,                            \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaEmptyIfAssertGuards) \
    X("assert_condition_blocks", RenderedCleanupRuleFamily::kGuard,                            \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaAssertConditionBlocks) \
    X("named_nil_asserts", RenderedCleanupRuleFamily::kGuard,                                  \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaNamedNilAsserts) \
    X("impossible_asserts", RenderedCleanupRuleFamily::kGuard,                                 \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaImpossibleAsserts) \
    X("localize_first_assignments", RenderedCleanupRuleFamily::kLocal,                        \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::LocalizeRenderedLuaFirstAssignments) \
    X("localize_function_locals", RenderedCleanupRuleFamily::kLocal,                           \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::LocalizeRenderedLuaFunctionLocals) \
    X("branch_shadowed_locals", RenderedCleanupRuleFamily::kLocal,                             \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::SimplifyRenderedLuaBranchShadowedLocals) \
    X("redundant_local_redeclarations", RenderedCleanupRuleFamily::kLocal,                    \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaRedundantLocalRedeclarations)                             \
    X("if_branch_shadow_local_carries", RenderedCleanupRuleFamily::kLocal,                    \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries)                               \
    X("immediate_identifier_carry_locals", RenderedCleanupRuleFamily::kLocal,                 \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals)                           \
    X("immediate_expression_carry_locals", RenderedCleanupRuleFamily::kLocal,                 \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals)                           \
    X("immediate_member_carry_locals", RenderedCleanupRuleFamily::kLocal,                     \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals)                               \
    X("immediate_iterator_argument_carry_locals", RenderedCleanupRuleFamily::kLocal,          \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals)                     \
    X("temporary_identifier_carries", RenderedCleanupRuleFamily::kLocal,                      \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaTemporaryIdentifierCarries)                               \
    X("redundant_colon_self_arguments", RenderedCleanupRuleFamily::kCall,                     \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments)                              \
    X("straight_line_single_use_temps", RenderedCleanupRuleFamily::kLocal,                    \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps)                               \
    X("immediate_boolean_alias_carries", RenderedCleanupRuleFamily::kLocal,                   \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaImmediateBooleanAliasCarries)                             \
    X("nearby_boolean_alias_locals", RenderedCleanupRuleFamily::kLocal,                       \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaNearbyBooleanAliasLocals)                                 \
    X("escaping_boolean_alias_uses", RenderedCleanupRuleFamily::kLocal,                       \
      RenderedCleanupRuleScope::kBoth, nullptr,                                                \
      refii::lua::SimplifyRenderedLuaEscapingBooleanAliasUses)                                 \
    X("expand_singleton_top_level_table_literals", RenderedCleanupRuleFamily::kTable,         \
      RenderedCleanupRuleScope::kAggressiveOnly, HasTableAliasCleanupSignals,                  \
      refii::lua::ExpandRenderedLuaSingletonTopLevelTableLiterals)                             \
    X("expand_large_qualified_table_literals", RenderedCleanupRuleFamily::kTable,             \
      RenderedCleanupRuleScope::kAggressiveOnly, HasTableAliasCleanupSignals,                  \
      refii::lua::ExpandRenderedLuaLargeQualifiedTableLiterals)                                \
    X("normalize_whitespace", RenderedCleanupRuleFamily::kMisc,                                \
      RenderedCleanupRuleScope::kBoth, nullptr, refii::lua::NormalizeRenderedLuaWhitespace)

#define REFII_BUILD_RULE(ID, FAMILY, SCOPE, PRECHECK, FN) \
    RenderedCleanupRuleDescriptor{ID, FAMILY, SCOPE, PRECHECK, FN},

constexpr auto kSharedCleanupRules =
    std::to_array<RenderedCleanupRuleDescriptor>({
        REFII_SHARED_RENDERED_CLEANUP_RULES(REFII_BUILD_RULE)
    });

#undef REFII_BUILD_RULE

}  // namespace

void ApplySharedCleanupRuleTableSteps(
    RenderedLuaCleanupProfile profile,
    std::vector<std::string>* lines,
    const SharedCleanupRuleTableCallbacks& callbacks,
    const RenderedCleanupRuleVisitor& apply_rule) {
    if (lines == nullptr) {
        return;
    }

    const SharedCleanupRuleTableCallbacks* previous_callbacks = g_active_callbacks;
    g_active_callbacks = &callbacks;
    struct CallbackScope {
        const SharedCleanupRuleTableCallbacks* previous = nullptr;
        ~CallbackScope() {
            g_active_callbacks = previous;
        }
    } callback_scope{previous_callbacks};

    // Evaluate prechecks against the initial snapshot for this cleanup pass so
    // cached decisions stay deterministic even as rules mutate `lines`.
    const std::vector<std::string> precheck_lines = *lines;
    using PrecheckFn = bool (*)(const std::vector<std::string>&);
    std::vector<std::pair<PrecheckFn, bool>> precheck_cache;
    precheck_cache.reserve(8);
    const auto precheck_passes = [&](PrecheckFn precheck) {
        if (precheck == nullptr) {
            return true;
        }
        for (const auto& entry : precheck_cache) {
            if (entry.first == precheck) {
                return entry.second;
            }
        }
        const bool result = precheck(precheck_lines);
        precheck_cache.emplace_back(precheck, result);
        return result;
    };

    if (profile ==
        refii::lua::internal::RenderedLuaCleanupProfile::kAggressiveCandidateRefinement) {
        for (const RenderedCleanupRuleDescriptor& rule : kAggressivePreludeRules) {
            if (!precheck_passes(rule.precheck)) {
                continue;
            }
            apply_rule(rule);
        }
    }

    for (const RenderedCleanupRuleDescriptor& rule : kSharedCleanupRules) {
        if (!IsRuleEnabledForProfile(rule, profile)) {
            continue;
        }
        if (!precheck_passes(rule.precheck)) {
            continue;
        }
        apply_rule(rule);
    }
}

#undef REFII_SHARED_RENDERED_CLEANUP_RULES

}  // namespace refii::lua::internal
