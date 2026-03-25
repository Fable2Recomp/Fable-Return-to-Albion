#pragma once

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/jump_render.h"
#include "tools/decompiler/render_state.h"

#include <string_view>

namespace refii::lua {

struct LuaBranchLoopRenderContext {
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    LuaRangeRenderer render_range;
    LuaInvalidateValuesRenderer invalidate_values;
    LuaFinalizePendingJumpRenderer finalize_pending_jump;
};

LuaJumpRenderResult TryRenderLuaBackJumpLoopBranch(
    const LuaBranchLoopRenderContext& context,
    const decompiler::LuaBackJumpLoopBlock& block,
    std::string_view condition,
    int invalidate_line,
    std::string* error);

LuaJumpRenderResult TryRenderLuaPreambleLoopBranch(const LuaBranchLoopRenderContext& context,
                                                   std::string_view condition,
                                                   int body_start,
                                                   int body_end,
                                                   int continue_target,
                                                   int exit_target,
                                                   int invalidate_line,
                                                   int next_current,
                                                   std::string* error);

}  // namespace refii::lua
