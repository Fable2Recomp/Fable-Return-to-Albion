#pragma once

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/jump_render.h"
#include "tools/decompiler/render_state.h"

#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

using LuaConditionalWrittenRegisterCollector =
    std::function<std::vector<bool>(int, int)>;
using LuaConditionalTerminalStatementChecker = std::function<bool(std::size_t)>;

struct LuaConditionalBlockRenderContext {
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    LuaRangeRenderer render_range;
    LuaConditionalWrittenRegisterCollector collect_written_registers_in_range;
    LuaConditionalTerminalStatementChecker last_appended_statement_is_terminal;
    LuaInvalidateValuesRenderer invalidate_values;
    LuaFinalizePendingJumpRenderer finalize_pending_jump;
};

LuaJumpRenderResult TryRenderLuaSharedTailConditionalBlock(
    const LuaConditionalBlockRenderContext& context,
    const decompiler::LuaSharedTailConditionalBlock& block,
    std::string_view condition,
    const LuaBranchStateSnapshot& saved_state,
    const LuaBranchStateSnapshot& then_entry_state,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error);

LuaJumpRenderResult TryRenderLuaConditionalBlock(
    const LuaConditionalBlockRenderContext& context,
    const decompiler::LuaConditionalBlock& block,
    std::string_view condition,
    const LuaBranchStateSnapshot& saved_state,
    const LuaBranchStateSnapshot& then_entry_state,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error);

}  // namespace refii::lua
