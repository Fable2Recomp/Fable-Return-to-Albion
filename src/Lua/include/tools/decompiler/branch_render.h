#pragma once

#include "tools/decompiler/jump_render.h"
#include "tools/decompiler/render_state.h"

#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

using LuaBranchPathToJoinEvaluator =
    std::function<bool(int, int, std::vector<StraightLineValue>*, std::string*)>;
using LuaPreviewSideEffectFreeChecker = std::function<bool(int, int)>;
using LuaWrittenRegisterCollector = std::function<std::vector<bool>(int, int)>;
using LuaTerminalStatementChecker = std::function<bool(std::size_t)>;

struct LuaBranchRenderContext {
    const std::vector<Lua51Instruction>& instructions;
    const decompiler::Lua51ControlFlowGraph& control_flow_graph;
    const LuaPreviewRenderContext* preview_context = nullptr;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    int range_start_line = 1;
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    LuaInlineRangeEvaluator evaluate_inline_range;
    LuaBranchPathToJoinEvaluator evaluate_branch_path_to_join;
    LuaPreviewSideEffectFreeChecker preview_range_is_side_effect_free;
    LuaConditionRenderer render_condition;
    LuaRangeRenderer render_range;
    LuaWrittenRegisterCollector collect_written_registers_in_range;
    LuaTerminalStatementChecker last_appended_statement_is_terminal;
    LuaInvalidateValuesRenderer invalidate_values;
    LuaFinalizePendingJumpRenderer finalize_pending_jump;
    bool trace_render = false;
    LuaRenderTrace trace;
};

LuaJumpRenderResult TryRenderLuaConditionalControlFlow(const LuaBranchRenderContext& context,
                                                       int current,
                                                       int end_line,
                                                       int loop_continue_target,
                                                       int loop_exit_target,
                                                       int line,
                                                       std::string* error);

}  // namespace refii::lua
