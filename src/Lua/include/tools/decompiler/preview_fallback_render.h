#pragma once

#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/straight_line_render.h"

#include <vector>

namespace refii::lua {

struct LuaPreviewRenderContext;

struct LuaPreviewFallbackContext {
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    const LuaPreviewRenderContext* preview_context = nullptr;
    const std::vector<bool>* initial_declared = nullptr;
    LuaInlineRangeEvaluator evaluate_inline_range;
};

bool TryMergeLuaPreviewRangeFallback(const LuaPreviewFallbackContext& context,
                                     int fallback_start,
                                     int fallback_end,
                                     int line,
                                     const std::vector<StraightLineValue>& values,
                                     int preferred_reg,
                                     bool require_temporary_declaration,
                                     StraightLineValue* merged_value,
                                     const StraightLineValue* initial_target_value = nullptr);

}  // namespace refii::lua
