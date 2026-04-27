#include "tools/lua_decompiler_internal.h"

namespace refii::lua::internal {

bool RunLuaRepairStageSequence(const LuaRepairStageCallbacks& callbacks) {
    const auto run_stage = [&](bool (*stage)(void*)) {
        if (IsCurrentDecompileRepairDeadlineExceeded()) {
            return false;
        }
        return stage != nullptr ? stage(callbacks.context) : false;
    };

    bool changed = false;
    changed |= run_stage(callbacks.run_early_structural_preclean);
    const bool owner_stage_changed = run_stage(callbacks.run_owner_cleanup);
    changed |= owner_stage_changed;
    const bool quest_stage_changed = run_stage(callbacks.run_quest_wait_repairs);
    changed |= quest_stage_changed;
    if (quest_stage_changed) {
        changed |= run_stage(callbacks.run_owner_cleanup);
    }

    return changed;
}

}  // namespace refii::lua::internal
