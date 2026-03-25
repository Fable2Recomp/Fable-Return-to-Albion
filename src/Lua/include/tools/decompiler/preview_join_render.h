#pragma once

#include "tools/decompiler/jump_render.h"
#include "tools/decompiler/preview_render.h"

#include <functional>
#include <string_view>

namespace refii::lua {

using LuaPreviewJoinSideEffectFreeChecker = std::function<bool(int, int)>;

bool ShouldAttemptLuaPreviewStatementJoin(int then_start_line,
                                          int then_end_line,
                                          int else_start_line,
                                          int else_end_line,
                                          int statement_line);

LuaJumpRenderResult EmitLuaPreviewStatementJoin(std::vector<std::string>* lines,
                                                const LuaPreviewRenderContext& preview_context,
                                                std::vector<StraightLineValue>* values,
                                                std::vector<bool>* declared,
                                                std::string_view condition,
                                                const LuaPreviewStatementJoin& join,
                                                int register_count,
                                                const LuaInvalidateValuesRenderer& invalidate_values,
                                                int invalidate_line,
                                                int next_current);

LuaJumpRenderResult TryBuildAndEmitLuaPreviewStatementJoin(
    std::vector<std::string>* lines,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int then_end_line,
    int else_start_line,
    int else_end_line,
    int statement_line,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line,
    int next_current);

LuaJumpRenderResult TryBuildAndEmitLuaPreviewStatementJoinToLine(
    std::vector<std::string>* lines,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int else_start_line,
    int join_line,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line,
    int next_current);

LuaJumpRenderResult TryApplyLuaPreviewConditionalJoinShortcuts(
    std::vector<std::string>* lines,
    const decompiler::Lua51ControlFlowGraph& control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    int register_count,
    const LuaPreviewJoinSideEffectFreeChecker& preview_range_is_side_effect_free,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line);

LuaJumpRenderResult TryApplyLuaPreviewCallJoinShortcut(
    std::vector<std::string>* lines,
    const std::vector<Lua51Instruction>& instructions,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values);

}  // namespace refii::lua
