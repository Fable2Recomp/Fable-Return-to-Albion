#include "tools/decompiler/rendered/shared_cleanup_rules.h"

namespace refii::lua::internal {

bool IsRuleEnabledForProfile(const RenderedCleanupRuleDescriptor& rule,
                             RenderedLuaCleanupProfile profile) {
    if (rule.scope == RenderedCleanupRuleScope::kBoth) {
        return true;
    }
    if (rule.scope == RenderedCleanupRuleScope::kAggressiveOnly) {
        return profile == RenderedLuaCleanupProfile::kAggressiveCandidateRefinement;
    }
    return profile == RenderedLuaCleanupProfile::kValidatedOutput;
}

}  // namespace refii::lua::internal
