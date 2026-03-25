#pragma once

#include "tools/decompiler/conditional_block_render.h"
#include "tools/decompiler/jump_render.h"

#include <string_view>
#include <vector>

namespace refii::lua {

struct LuaStructuredConditionalBranchContext {
    const decompiler::Lua51ControlFlowGraph& control_flow_graph;
    const std::vector<Lua51Instruction>& instructions;
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

LuaJumpRenderResult TryRenderLuaStructuredConditionalBranch(
    const LuaStructuredConditionalBranchContext& context,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    int combined_then_start,
    std::string_view condition,
    std::string_view combined_condition,
    const std::vector<StraightLineValue>& then_entry_values,
    int loop_continue_target,
    int loop_exit_target,
    int invalidate_line,
    std::string* error);

}  // namespace refii::lua
