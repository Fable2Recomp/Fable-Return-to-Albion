#pragma once

#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/control_flow_graph.h"
#include "tools/decompiler/preview_render.h"
#include "tools/decompiler/straight_line_render.h"

#include <functional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua {

struct LuaLoopTargetScope {
    std::vector<std::pair<int, int>>& stack;
    bool active;

    LuaLoopTargetScope(std::vector<std::pair<int, int>>& stack_ref,
                       int continue_target,
                       int exit_target);
    ~LuaLoopTargetScope();
};

int FindAncestorLuaLoopTarget(const std::vector<std::pair<int, int>>& loop_target_stack,
                              int target,
                              int current_continue_target,
                              int current_exit_target);

bool FinalizePendingLuaJump(std::vector<std::string>* lines,
                            int* pending_jump_target,
                            int current_continue_target,
                            int current_exit_target);

using LuaReturnValuesRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaRangeRenderer = std::function<bool(int, int, int, int)>;
using LuaConditionRenderer = std::function<std::string(const Lua51Instruction&, int)>;
using LuaInvalidateValuesRenderer = std::function<void(int)>;
using LuaFinalizePendingJumpRenderer = std::function<bool(int, int)>;
using LuaRenderTrace = std::function<void(std::string_view)>;

struct LuaJumpRenderContext {
    const std::vector<Lua51Instruction>& instructions;
    const std::vector<std::size_t>& instruction_output_offsets;
    const decompiler::Lua51ControlFlowGraph& control_flow_graph;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    int range_start_line = 1;
    std::size_t range_base_offset = 0;
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    LuaPreviewCallArgumentsRenderer call_arguments_for;
    LuaReturnValuesRenderer return_values_for;
    LuaInlineRangeEvaluator evaluate_inline_range;
    LuaRangeRenderer render_range;
    LuaConditionRenderer render_condition;
    LuaInvalidateValuesRenderer invalidate_values;
    int* pending_jump_target = nullptr;
    const std::vector<std::pair<int, int>>* loop_target_stack = nullptr;
    bool trace_render = false;
    LuaRenderTrace trace;
};

enum class LuaJumpRenderDisposition {
    kError,
    kContinue,
    kReturn,
};

struct LuaJumpRenderResult {
    LuaJumpRenderDisposition disposition = LuaJumpRenderDisposition::kError;
    int next_current = -1;
};

LuaJumpRenderResult TryRenderLuaJumpControlFlow(const LuaJumpRenderContext& context,
                                                int current,
                                                int end_line,
                                                int loop_continue_target,
                                                int loop_exit_target,
                                                int line,
                                                std::string* error);

}  // namespace refii::lua
