#pragma once

#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/control_flow_graph.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/render_state.h"
#include "tools/decompiler/straight_line_render.h"

#include <cstdint>
#include <functional>
#include <string>
#include <unordered_map>
#include <vector>

namespace refii::lua {

using LuaPreviewCallArgumentsRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaPreviewChildRenderer = std::function<bool(int, std::string*)>;

struct LuaPreviewRenderCache {
    std::vector<std::vector<bool>> written_registers_for_line;
    std::vector<bool> written_registers_ready_for_line;
    std::unordered_map<std::uint64_t, std::string> materialization_names;
    std::unordered_map<std::uint64_t, int> materialization_lines;
    bool scoped_small_holdout_ready = false;
    bool scoped_small_holdout = false;
    std::unordered_map<std::uint64_t, LuaConditionalChainCommitResolution>
        conditional_commit_resolution_cache;
    std::unordered_map<std::uint64_t, bool> conditional_commit_resolution_ready;
    std::unordered_map<std::uint64_t, LuaConditionalRootCommitPacket>
        conditional_root_commit_packet_cache;
    std::unordered_map<std::uint64_t, bool> conditional_root_commit_packet_ready;
    bool profile_ready = false;
    LuaScriptPolicy profile_policy;
};

struct LuaPreviewRenderContext {
    const std::vector<Lua51Instruction>& instructions;
    const decompiler::Lua51ControlFlowGraph* control_flow_graph = nullptr;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    LuaPreviewCallArgumentsRenderer call_arguments_for;
    LuaPreviewChildRenderer render_child;
    LuaPreviewRenderCache* cache = nullptr;
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

std::vector<bool> GetLuaPreviewWrittenRegistersForLine(const LuaPreviewRenderContext& context,
                                                       int line);

bool LuaPreviewWritesRegisterAtLine(const LuaPreviewRenderContext& context, int reg, int line);

std::string GetLuaPreviewFallbackMaterializationName(const LuaPreviewRenderContext& context,
                                                     int reg,
                                                     int line);

int FindLuaPreviewFallbackMaterializationLine(const LuaPreviewRenderContext& context,
                                              int reg,
                                              int assignment_line);

bool TryResolveLuaPreviewConditionalCommitForRoot(
    const LuaPreviewRenderContext& context,
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    LuaConditionalChainCommitResolution* resolution_out);

bool TryResolveLuaPreviewConditionalRootCommitPacket(
    const LuaPreviewRenderContext& context,
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    int fallback_branch_target_line,
    int fallback_join_line,
    LuaConditionalRootCommitPacket* packet_out);

}  // namespace refii::lua
