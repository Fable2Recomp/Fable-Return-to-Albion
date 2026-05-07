#include "tools/decompiler/rendered/shared_cleanup_rules.h"

#include "tools/decompiler/rendered/repair_state_keys.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <array>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

bool TryApplySharedUnhandledIfCommentCleanup(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();

    bool changed = false;
    constexpr std::array<bool (*)(std::vector<std::string>*), 13> kUnhandledIfSteps = {{
        refii::lua::SimplifyRenderedLuaUnhandledStateIfComments,
        refii::lua::SimplifyRenderedLuaUnhandledIfCommentBranchChains,
        refii::lua::SimplifyRenderedLuaUnhandledIfCommentLeadingSuffixes,
        refii::lua::SimplifyRenderedLuaUnhandledIfCommentNestedTailChains,
        refii::lua::SimplifyRenderedLuaUnhandledIfCommentNilGuardSplits,
        refii::lua::SimplifyRenderedLuaUnhandledSparseNilGuardBranches,
        refii::lua::SimplifyRenderedLuaUnhandledOuterOrNestedElseIfChains,
        refii::lua::SimplifyRenderedLuaUnhandledFlaggedElseIfChains,
        refii::lua::SimplifyRenderedLuaUnhandledStateYieldCommentLoops,
        refii::lua::SimplifyRenderedLuaUnhandledCurrentStateLeadingGuards,
        refii::lua::SimplifyRenderedLuaUnhandledNestedCurrentStateElseIfBranches,
        refii::lua::SimplifyRenderedLuaUnhandledNestedCurrentStateGuardBranches,
        refii::lua::SimplifyRenderedLuaUnhandledRetargetBreakYieldLoops,
    }};
    for (const auto step : kUnhandledIfSteps) {
        changed |= step(lines);
    }

    if (!changed) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "rendered_unhandled_if_cleanup");
    }
    return true;
}

bool TryApplySharedRenderedOutputCleanup(std::string& decompiled_source,
                                         std::string& validation_error,
                                         std::string_view validation_label,
                                         std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(decompiled_source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    if (!ApplySharedRenderedLuaCleanup(
            lines,
            refii::lua::internal::RenderedLuaCleanupProfile::kAggressiveCandidateRefinement)) {
        return false;
    }
    text_context.MarkLinesMutated();

    std::string candidate_source(text_context.EnsureSource());
    if (candidate_source == decompiled_source) {
        return false;
    }

    const LuaOutputQualitySummary baseline_quality =
        SummarizeLuaOutputQuality(decompiled_source);
    const std::string baseline_family_state =
        BuildRepairFamilyStateKey(decompiled_source, validation_error);
    std::string candidate_error = text_context.ValidateCached(validation_label);
    const LuaOutputQualitySummary candidate_quality =
        SummarizeLuaOutputQuality(candidate_source);
    const std::string candidate_family_state =
        BuildRepairFamilyStateKey(candidate_source, candidate_error);
    const bool same_family = candidate_family_state == baseline_family_state;
    if (!candidate_error.empty() &&
        !IsValidationOutcomeBetter(validation_error, candidate_error) &&
        !(same_family && IsLuaOutputQualityBetter(baseline_quality, candidate_quality))) {
        return false;
    }

    decompiled_source = std::move(candidate_source);
    validation_error = std::move(candidate_error);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          validation_error.empty() ? "rendered_cleanup_prepass_validated"
                                                   : "rendered_cleanup_prepass_progress");
    }
    return true;
}

} // namespace refii::lua::internal
