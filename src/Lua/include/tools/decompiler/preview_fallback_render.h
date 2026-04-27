#pragma once

#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/straight_line_render.h"

#include <vector>

namespace refii::lua {

struct LuaPreviewRenderContext;

struct LuaPreviewFallbackContext {
    const decompiler::Lua51ControlFlowGraph* control_flow_graph = nullptr;
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

bool TryMergeLuaPreviewBranchPathFallback(const LuaPreviewFallbackContext& context,
                                          int fallback_start,
                                          int join_line,
                                          int line,
                                          const std::vector<StraightLineValue>& values,
                                          int preferred_reg,
                                          bool require_temporary_declaration,
                                          StraightLineValue* merged_value,
                                          const StraightLineValue* initial_target_value = nullptr);

bool TryEvaluateLuaPreviewBranchPathValue(const LuaPreviewFallbackContext& context,
                                          int start_line,
                                          int join_line,
                                          int line,
                                          const std::vector<StraightLineValue>& values,
                                          int target_reg,
                                          StraightLineValue* value,
                                          const StraightLineValue* initial_target_value = nullptr);

}  // namespace refii::lua
