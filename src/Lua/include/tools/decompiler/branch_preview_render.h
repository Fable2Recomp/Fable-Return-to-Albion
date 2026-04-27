#pragma once

#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/conditional_block_render.h"
#include "tools/decompiler/straight_line_render.h"

#include <cstddef>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

struct LuaPreviewRenderContext;

using LuaBranchPreviewSideEffectFreeChecker = std::function<bool(int, int)>;

struct LuaBranchPreviewFallbackContext {
    const decompiler::Lua51ControlFlowGraph* control_flow_graph = nullptr;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    const LuaPreviewRenderContext* preview_context = nullptr;
    const LuaConditionalRootCommitPacket* precomputed_root_packet = nullptr;
    LuaInlineRangeEvaluator evaluate_inline_range;
    LuaBranchPreviewSideEffectFreeChecker preview_range_is_side_effect_free;
};

struct LuaBranchPreviewSharedTailContext {
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    const std::vector<Lua51Instruction>& instructions;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    LuaInlineRangeEvaluator evaluate_inline_range;
    LuaRangeRenderer render_range;
    LuaConditionalWrittenRegisterCollector collect_written_registers_in_range;
    LuaConditionalTerminalStatementChecker last_appended_statement_is_terminal;
    LuaInvalidateValuesRenderer invalidate_values;
    LuaFinalizePendingJumpRenderer finalize_pending_jump;
};

struct LuaBranchPreviewShortcutContext {
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    const decompiler::Lua51ControlFlowGraph& control_flow_graph;
    const std::vector<Lua51Instruction>& instructions;
    const LuaPreviewRenderContext* preview_context = nullptr;
    const LuaConditionalRootCommitPacket* precomputed_root_packet = nullptr;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    LuaInlineRangeEvaluator evaluate_inline_range;
    LuaBranchPreviewSideEffectFreeChecker preview_range_is_side_effect_free;
    LuaRangeRenderer render_range;
    LuaConditionalWrittenRegisterCollector collect_written_registers_in_range;
    LuaConditionalTerminalStatementChecker last_appended_statement_is_terminal;
    LuaInvalidateValuesRenderer invalidate_values;
    LuaFinalizePendingJumpRenderer finalize_pending_jump;
};

bool TryApplyLuaConditionalPreviewFallback(const LuaBranchPreviewFallbackContext& context,
                                           const Lua51Instruction& instruction,
                                           int current,
                                           int branch_target,
                                           int end_line,
                                           int instruction_count,
                                           int line,
                                           std::vector<std::string>* lines,
                                           std::vector<StraightLineValue>* values,
                                           std::vector<bool>* declared,
                                           int* next_current);

LuaJumpRenderResult TryRenderLuaSharedTailPreviewConditional(
    const LuaBranchPreviewSharedTailContext& context,
    int branch_target,
    int combined_then_start,
    int end_line,
    std::string_view combined_condition,
    const std::vector<StraightLineValue>& then_entry_values,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error);

LuaJumpRenderResult TryApplyLuaConditionalPreviewShortcuts(
    const LuaBranchPreviewShortcutContext& context,
    const Lua51Instruction& instruction,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    std::string_view condition,
    std::string_view combined_condition,
    int combined_then_start,
    const std::vector<StraightLineValue>& then_entry_values,
    bool skip_combined_preview_join_shortcuts,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error);

void ResetLuaNativeStatementTempCommitCounter();
std::size_t ConsumeLuaNativeStatementTempCommitCounter();
std::size_t ConsumeLuaNativeCallValueOwnershipCommitCounter();

}  // namespace refii::lua
