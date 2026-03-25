#pragma once

#include "tools/decompiler/jump_render.h"

#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

enum class LuaLoopRenderDisposition {
    kNotHandled,
    kError,
    kContinue,
    kReturn,
};

struct LuaLoopRenderResult {
    LuaLoopRenderDisposition disposition = LuaLoopRenderDisposition::kNotHandled;
    int next_current = -1;
};

struct LuaLoopRenderContext {
    const std::vector<Lua51Instruction>& instructions;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    LuaPreviewCallArgumentsRenderer call_arguments_for;
    LuaInlineRangeEvaluator evaluate_inline_range;
    LuaConditionRenderer render_condition;
    LuaRangeRenderer render_range;
    LuaInvalidateValuesRenderer invalidate_values;
    LuaFinalizePendingJumpRenderer finalize_pending_jump;
    bool trace_render = false;
    LuaRenderTrace trace;
};

LuaLoopRenderResult TryRenderLuaRangeEntryLoop(const LuaLoopRenderContext& context,
                                               int start_line,
                                               int end_line,
                                               int loop_continue_target,
                                               int loop_exit_target,
                                               std::string* error);

LuaLoopRenderResult TryRenderLuaLoopControlFlow(const LuaLoopRenderContext& context,
                                                int current,
                                                int end_line,
                                                int loop_continue_target,
                                                int loop_exit_target,
                                                int line,
                                                std::string* error);

}  // namespace refii::lua
