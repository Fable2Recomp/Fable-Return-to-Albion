#pragma once

#include "tools/decompiler/loop_render.h"

#include <string>

namespace refii::lua {

struct LuaLoopBlockRenderContext {
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    LuaRangeRenderer render_range;
    LuaInvalidateValuesRenderer invalidate_values;
    LuaFinalizePendingJumpRenderer finalize_pending_jump;
};

bool RenderLuaLoopBodyRange(const LuaLoopBlockRenderContext& context,
                            int body_start,
                            int body_end,
                            int continue_target,
                            int exit_target,
                            std::string* error);

LuaLoopRenderResult FinalizeLuaEndClosedLoop(const LuaLoopBlockRenderContext& context,
                                             int invalidate_line,
                                             int finalize_continue_target,
                                             int finalize_exit_target,
                                             int next_current,
                                             int resolved_next_current,
                                             int outer_continue_target,
                                             int outer_exit_target,
                                             LuaLoopRenderDisposition default_disposition,
                                             bool emit_break_on_outer_exit = true);

}  // namespace refii::lua
