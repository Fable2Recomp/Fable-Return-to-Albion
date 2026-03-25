#pragma once

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/straight_line_render.h"

#include <string>

namespace refii::lua {

struct LuaLoopBindingState {
    std::vector<StraightLineValue> saved_values;
    std::vector<bool> saved_declared;
};

bool PrepareLuaGenericForStatement(const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int register_count,
                                   const decompiler::LuaGenericForLoopBlock& block,
                                   std::string_view call_expression,
                                   std::vector<StraightLineValue>* values,
                                   std::vector<bool>* declared,
                                   LuaLoopBindingState* binding,
                                   std::string* statement,
                                   std::string* error);

bool PrepareLuaNumericForStatement(const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int register_count,
                                   const decompiler::LuaNumericForLoopBlock& block,
                                   std::string_view initial,
                                   std::string_view limit,
                                   std::string_view step,
                                   std::vector<StraightLineValue>* values,
                                   std::vector<bool>* declared,
                                   LuaLoopBindingState* binding,
                                   std::string* statement,
                                   std::string* error);

void RestoreLuaLoopBindingState(std::vector<StraightLineValue>* values,
                                std::vector<bool>* declared,
                                LuaLoopBindingState* binding);

std::string BuildLuaRepeatFooter(std::string_view condition_text);

}  // namespace refii::lua
