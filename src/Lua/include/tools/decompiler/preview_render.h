#pragma once

#include "tools/decompiler/control_flow_graph.h"
#include "tools/decompiler/render_state.h"
#include "tools/decompiler/straight_line_render.h"

#include <functional>
#include <string>
#include <vector>

namespace refii::lua {

using LuaPreviewCallArgumentsRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaPreviewChildRenderer = std::function<bool(int, std::string*)>;

struct LuaPreviewRenderContext {
    const std::vector<Lua51Instruction>& instructions;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    LuaPreviewCallArgumentsRenderer call_arguments_for;
    LuaPreviewChildRenderer render_child;
};

struct LuaPreviewStatementJoin {
    std::vector<StraightLineValue> then_values;
    std::vector<StraightLineValue> else_values;
    std::vector<bool> then_declared;
    std::vector<bool> else_declared;
    std::vector<bool> then_written_registers;
    std::vector<bool> else_written_registers;
    bool then_requires_full_tail_render = false;
    bool else_requires_full_tail_render = false;
    int statement_line = -1;
};

bool EvaluateLuaInlineRange(const LuaPreviewRenderContext& context,
                            int start_line,
                            int end_line,
                            std::vector<StraightLineValue>* preview_values,
                            std::vector<bool>* preview_declared,
                            std::string* preview_error);

bool EvaluateLuaBranchPathToJoin(const LuaPreviewRenderContext& context,
                                 int start_line,
                                 int join_line,
                                 std::vector<StraightLineValue>* preview_values,
                                 std::vector<bool>* preview_declared,
                                 std::string* preview_error);

bool TryBuildLuaPreviewStatementJoin(const LuaPreviewRenderContext& context,
                                     const std::vector<StraightLineValue>& base_values,
                                     const std::vector<bool>& base_declared,
                                     int then_start_line,
                                     int then_end_line,
                                     int else_start_line,
                                     int else_end_line,
                                     int statement_line,
                                     LuaPreviewStatementJoin* join,
                                     std::string* preview_error);

bool TryBuildLuaPreviewStatementJoinToLine(const LuaPreviewRenderContext& context,
                                           const std::vector<StraightLineValue>& base_values,
                                           const std::vector<bool>& base_declared,
                                           int then_start_line,
                                           int else_start_line,
                                           int join_line,
                                           LuaPreviewStatementJoin* join,
                                           std::string* preview_error);

bool TryRenderLuaPreviewStatementJoin(const LuaPreviewRenderContext& context,
                                      const LuaPreviewStatementJoin& join,
                                      std::string* then_statement,
                                      std::string* else_statement);

bool TryBuildLuaPreviewStatementJoinStates(const LuaPreviewRenderContext& context,
                                           const LuaPreviewStatementJoin& join,
                                           LuaBranchStateSnapshot* then_state,
                                           LuaBranchStateSnapshot* else_state);

bool IsLuaPreviewRangeSideEffectFree(const LuaPreviewRenderContext& context,
                                     int start_line,
                                     int end_line);

bool RenderLuaSingleStatementFromPreview(const LuaPreviewRenderContext& context,
                                         const std::vector<StraightLineValue>& state_values,
                                         const std::vector<bool>* state_declared,
                                         int statement_line,
                                         std::string* statement);

}  // namespace refii::lua
