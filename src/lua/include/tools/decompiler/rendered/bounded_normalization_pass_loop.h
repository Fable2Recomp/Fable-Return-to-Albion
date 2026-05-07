#pragma once

#include "tools/decompiler/repair/lua_decompile_repair_context.h"

#include <algorithm>
#include <cstddef>

namespace refii::lua::internal {

template <typename PassFn>
bool RunBoundedNormalizationPassLoop(int max_passes, PassFn&& run_single_pass) {
    bool changed = false;
    const int pass_cap = std::max(max_passes, 1);
    for (int pass = 0; pass < pass_cap; ++pass) {
        if (IsCurrentDecompileRepairDeadlineExceeded()) {
            break;
        }
        if (!run_single_pass(pass)) {
            break;
        }
        changed = true;
    }
    return changed;
}

inline int ComputeAdaptiveNormalizationPassLimit(std::size_t line_count,
                                                 int base_limit) {
    if (base_limit <= 0) {
        return 0;
    }

    int limit = base_limit;
    if (line_count >= 12000) {
        limit = std::min(limit, std::max(6, base_limit / 8));
    } else if (line_count >= 8000) {
        limit = std::min(limit, std::max(8, base_limit / 6));
    } else if (line_count >= 4000) {
        limit = std::min(limit, std::max(12, base_limit / 4));
    } else if (line_count >= 2500) {
        limit = std::min(limit, std::max(16, base_limit / 3));
    } else if (line_count >= 1200) {
        limit = std::min(limit, std::max(24, base_limit / 2));
    }
    return std::max(1, limit);
}

}  // namespace refii::lua::internal
