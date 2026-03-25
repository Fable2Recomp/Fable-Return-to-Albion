#pragma once

#include "tools/decompiler/control_flow_graph.h"
#include "tools/decompiler/straight_line_render.h"

#include <functional>
#include <string>
#include <vector>

namespace refii::lua {

using LuaInlineRangeEvaluator =
    std::function<bool(int, int, std::vector<StraightLineValue>*, std::string*)>;

std::string RenderLuaConditionWithValues(const std::vector<StraightLineValue>& state_values,
                                         const RegisterDeclarations& declarations,
                                         const Lua51Function& function,
                                         const Lua51Instruction& instruction,
                                         int line);

void AppendLuaChainedAndConditions(std::string* combined_condition,
                                   int* combined_then_start,
                                   int branch_target,
                                   const std::vector<Lua51Instruction>& instructions,
                                   std::vector<StraightLineValue>* preview_values,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   const LuaInlineRangeEvaluator& evaluate_inline_range);

}  // namespace refii::lua
