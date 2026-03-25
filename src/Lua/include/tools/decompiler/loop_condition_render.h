#pragma once

#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/control_flow_blocks.h"

#include <string>
#include <vector>

namespace refii::lua {

struct LuaLoopConditionRenderContext {
    const std::vector<Lua51Instruction>& instructions;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    LuaInlineRangeEvaluator evaluate_inline_range;
};

bool TryRenderLuaLoopConditionText(const LuaLoopConditionRenderContext& context,
                                   int preview_start,
                                   int preview_end,
                                   const Lua51Instruction& condition_instruction,
                                   int condition_line,
                                   std::vector<StraightLineValue>* values,
                                   std::string* text,
                                   std::string* error);

bool TryRenderLuaChainedLoopConditions(
    const LuaLoopConditionRenderContext& context,
    const decompiler::LuaChainedConditionLoopBlock& block,
    std::vector<StraightLineValue>* values,
    std::vector<std::string>* conditions,
    std::string* error);

std::string BuildLuaWhileHeader(const std::vector<std::string>& conditions);

}  // namespace refii::lua
