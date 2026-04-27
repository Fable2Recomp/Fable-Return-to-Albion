#pragma once

#include "tools/decompiler/control_flow_graph.h"

#include <functional>

namespace refii::lua::decompiler {

struct LuaConditionalBlock {
    int then_start = -1;
    int then_end = -1;
    int else_start = -1;
    int else_end = -1;
    int after_line = -1;
    int shared_continue_target = -1;
    bool has_else = false;
    bool then_exits_loop = false;
    bool prefer_elseif_chain = false;
};

struct LuaConditionalOwnership {
    bool classified = false;
    bool unambiguous = false;
    bool early_bailout = false;
    bool has_foreign_incoming_edge = false;
    bool has_unowned_loopback = false;
    bool allow_else = true;
    bool allow_elseif = false;
    int effective_join_line = -1;
    int region_start = -1;
    int region_end = -1;
};

struct LuaSharedTailConditionalBlock {
    int inner_condition_line = -1;
    int inner_jump_line = -1;
    int shared_exit = -1;
    int then_start = -1;
    int then_end = -1;
    int else_start = -1;
    int else_end = -1;
    int after_line = -1;
    bool has_else = false;
};

struct LuaBackJumpLoopBlock {
    int body_start = -1;
    int body_end = -1;
    int continue_target = -1;
    int exit_target = -1;
    int after_line = -1;
};

struct LuaGenericForLoopBlock {
    int body_start = -1;
    int body_end = -1;
    int loop_check_line = -1;
    int loop_exit = -1;
    int base_register = -1;
    int first_loop_register = -1;
    int last_loop_register = -1;
};

struct LuaNumericForLoopBlock {
    int body_start = -1;
    int body_end = -1;
    int loop_exit = -1;
    int forloop_line = -1;
    int base_register = -1;
    int loop_var_register = -1;
};

struct LuaRepeatLoopBlock {
    int body_start = -1;
    int body_end = -1;
    int condition_line = -1;
    int after_line = -1;
};

struct LuaRangeEntryLoopBlock {
    int body_start = -1;
    int body_end = -1;
    int condition_line = -1;
    int condition_render_line = -1;
    int guard_jump_line = -1;
    int guard_target = -1;
    int resolved_guard_target = -1;
    int continue_target = -1;
    int exit_target = -1;
    int preamble_start = -1;
    bool ends_current_range = false;
    bool exits_parent = false;
};

struct LuaChainedConditionLoopBlock {
    int body_start = -1;
    int body_end = -1;
    int loop_back_line = -1;
    int default_loop_exit = -1;
    int loop_exit = -1;
    std::vector<int> segment_starts;
    std::vector<int> condition_lines;
};

struct LuaGuardedContinueBlock {
    int condition_line = -1;
    int condition_render_line = -1;
    int body_start = -1;
    int body_end = -1;
    int guard_target = -1;
    int resolved_guard_target = -1;
};

struct LuaTestSetLoopControlBlock {
    int branch_target = -1;
    int fallback_start = -1;
    int fallback_end = -1;
    int target_register = -1;
    int source_register = -1;
    bool invert_condition = false;
};

bool TryBuildLuaConditionalBlock(const Lua51ControlFlowGraph& graph,
                                 const std::vector<Lua51Instruction>& instructions,
                                 int conditional_line,
                                 int then_start,
                                 int branch_target,
                                 int end_line,
                                 int loop_continue_target,
                                 int loop_exit_target,
                                 LuaConditionalBlock* block);

bool TryClassifyLuaConditionalOwnership(const Lua51ControlFlowGraph& graph,
                                        const std::vector<Lua51Instruction>& instructions,
                                        int conditional_line,
                                        int then_start,
                                        int branch_target,
                                        int end_line,
                                        int loop_continue_target,
                                        int loop_exit_target,
                                        LuaConditionalOwnership* ownership);

bool TryClassifyLuaConditionalOwnershipForConsumedSpan(
    const Lua51ControlFlowGraph& graph,
    const std::vector<Lua51Instruction>& instructions,
    int conditional_line,
    int then_start,
    int branch_target,
    int end_line,
    int loop_continue_target,
    int loop_exit_target,
    int consumed_span_start,
    int consumed_span_end,
    LuaConditionalOwnership* ownership);

bool TryBuildLuaSharedTailConditionalBlock(const std::vector<Lua51Instruction>& instructions,
                                           int branch_target,
                                           int combined_then_start,
                                           int end_line,
                                           int loop_continue_target,
                                           LuaSharedTailConditionalBlock* block);

bool TryBuildLuaBackJumpLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                  int current,
                                  int branch_target,
                                  int end_line,
                                  LuaBackJumpLoopBlock* block);

bool TryBuildLuaGenericForLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                    int current,
                                    LuaGenericForLoopBlock* block);

bool TryBuildLuaNumericForLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                    int current,
                                    LuaNumericForLoopBlock* block);

bool TryBuildLuaRepeatLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                int current,
                                int end_line,
                                LuaRepeatLoopBlock* block);

bool TryBuildLuaRangeEntryLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                    int start_line,
                                    int end_line,
                                    int loop_continue_target,
                                    int loop_exit_target,
                                    LuaRangeEntryLoopBlock* block);

bool TryBuildLuaChainedConditionLoopBlock(
    const std::vector<Lua51Instruction>& instructions,
    int current,
    int end_line,
    int loop_continue_target,
    int loop_exit_target,
    const std::function<bool(int segment_start, int condition_line)>& accepts_condition_line,
    LuaChainedConditionLoopBlock* block);

bool TryBuildLuaGuardedContinueBlock(const std::vector<Lua51Instruction>& instructions,
                                     int current,
                                     int end_line,
                                     int loop_continue_target,
                                     LuaGuardedContinueBlock* block);

bool TryBuildLuaTestSetLoopControlBlock(const std::vector<Lua51Instruction>& instructions,
                                        int current,
                                        LuaTestSetLoopControlBlock* block);

}  // namespace refii::lua::decompiler
