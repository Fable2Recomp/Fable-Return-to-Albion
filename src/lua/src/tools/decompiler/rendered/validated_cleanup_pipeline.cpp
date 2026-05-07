#include "tools/decompiler/rendered/validated_cleanup_pipeline.h"

#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

RenderedLuaNonexactLightCleanupResult ApplyRenderedLuaNonexactLightCleanup(
    std::vector<std::string>* lines,
    bool allow_call_reassignment_self_compare_cleanup) {
    RenderedLuaNonexactLightCleanupResult result;
    if (lines == nullptr) {
        return result;
    }

    result.assigned_functions_changed =
        refii::lua::SimplifyRenderedLuaAssignedFunctionDeclarations(lines);
    result.branch_shadow_local_carries_changed =
        refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries(lines);
    result.call_reassignment_self_compare_changed =
        allow_call_reassignment_self_compare_cleanup &&
        refii::lua::SimplifyRenderedLuaCallReassignmentSelfCompareGuards(lines);
    result.explicit_self_method_arguments_changed =
        refii::lua::SimplifyRenderedLuaExplicitSelfMethodArguments(lines);
    result.redundant_colon_self_arguments_changed =
        refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments(lines);
    result.adjacent_duplicate_call_value_conditions_changed =
        refii::lua::SimplifyRenderedLuaAdjacentDuplicateCallValueConditions(lines);
    result.adjacent_duplicate_local_calls_changed =
        refii::lua::SimplifyRenderedLuaAdjacentDuplicateLocalCallDeclarations(lines);
    result.straight_line_single_use_temps_changed =
        refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps(lines);
    result.integral_float_literals_changed =
        refii::lua::SimplifyRenderedLuaIntegralFloatLiterals(lines);
    return result;
}

void AppendRenderedLuaNonexactLightCleanupNotes(
    const RenderedLuaNonexactLightCleanupResult& cleanup_result,
    std::string_view note_prefix,
    std::string* note) {
    if (note == nullptr || note_prefix.empty() || !cleanup_result.changed_any()) {
        return;
    }

    *note = JoinNotes(*note,
                      std::string(note_prefix) +
                          "_validated_nonexact_lightcleanup_repaired");
    if (cleanup_result.assigned_functions_changed) {
        *note = JoinNotes(*note,
                          std::string(note_prefix) +
                              "_validated_nonexact_assignedfunctions_repaired");
    }
    if (cleanup_result.branch_shadow_local_carries_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_branchshadowlocalcarries_repaired");
    }
    if (cleanup_result.call_reassignment_self_compare_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_callreassignment_selfcompare_repaired");
    }
    if (cleanup_result.explicit_self_method_arguments_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_explicitselfcalls_repaired");
    }
    if (cleanup_result.redundant_colon_self_arguments_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_redundantcolonself_repaired");
    }
    if (cleanup_result.adjacent_duplicate_call_value_conditions_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_duplicatecallvalueconditions_repaired");
    }
    if (cleanup_result.adjacent_duplicate_local_calls_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_duplicatelocalcalls_repaired");
    }
    if (cleanup_result.straight_line_single_use_temps_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_singleusetemps_repaired");
    }
    if (cleanup_result.integral_float_literals_changed) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                "_validated_nonexact_integralfloatliterals_repaired");
    }
}

}  // namespace refii::lua::internal
