#pragma once

#include "tools/decompiler/straight_line_render.h"

#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

using LuaStatementCallArgumentsRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaStatementReturnValuesRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaStatementSetListValuesRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaStatementChildRenderer = std::function<bool(int, std::string*)>;
using LuaStatementSetListBlockResolver =
    std::function<int(int, const Lua51Instruction&, int*, std::string*)>;
using LuaStatementTrace = std::function<void(std::string_view)>;

enum class LuaStatementRenderDisposition {
    kError,
    kContinue,
    kReturn,
};

struct LuaStatementRenderResult {
    LuaStatementRenderDisposition disposition = LuaStatementRenderDisposition::kError;
    int next_current = -1;
};

struct LuaStatementRenderContext {
    const std::vector<Lua51Instruction>& instructions;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    LuaStatementCallArgumentsRenderer call_arguments_for;
    LuaStatementReturnValuesRenderer return_values_for;
    LuaStatementSetListValuesRenderer setlist_values_for;
    LuaStatementChildRenderer render_child;
    LuaStatementSetListBlockResolver resolve_setlist_block;
    bool trace_render = false;
    LuaStatementTrace trace;
};

LuaStatementRenderResult TryRenderLuaStraightLineStatement(
    const LuaStatementRenderContext& context,
    int start_line,
    int end_line,
    int current,
    int line,
    std::string* error);

}  // namespace refii::lua
