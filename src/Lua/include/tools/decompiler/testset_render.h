#pragma once

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/preview_fallback_render.h"
#include "tools/decompiler/straight_line_render.h"

#include <string>

namespace refii::lua {

struct LuaTestSetFallbackContext {
    LuaPreviewFallbackContext preview_fallback;
};

StraightLineValue GetLuaTestSetInitialValue(const std::vector<StraightLineValue>& values,
                                            const LuaTestSetFallbackContext& context,
                                            const decompiler::LuaTestSetLoopControlBlock& block,
                                            int line);

bool TryMergeLuaTestSetFallbackValues(const LuaTestSetFallbackContext& context,
                                      const decompiler::LuaTestSetLoopControlBlock& block,
                                      int line,
                                      const std::vector<StraightLineValue>& values,
                                      StraightLineValue* merged_value);

bool TryMergeLuaTestSetBranchPathFallbackValues(
    const LuaTestSetFallbackContext& context,
    const decompiler::LuaTestSetLoopControlBlock& block,
    int line,
    const std::vector<StraightLineValue>& values,
    StraightLineValue* merged_value);

void AssignLuaTestSetTarget(std::vector<std::string>* lines,
                            std::vector<StraightLineValue>* values,
                            std::vector<bool>* declared,
                            const LuaTestSetFallbackContext& context,
                            const decompiler::LuaTestSetLoopControlBlock& block,
                            int line,
                            const StraightLineValue& initial_value,
                            bool force_materialize = false);

std::string BuildLuaTestSetFallbackCondition(const RegisterDeclarations& declarations,
                                             const Lua51Function& function,
                                             int target_register,
                                             int line,
                                             bool invert_condition);

}  // namespace refii::lua
