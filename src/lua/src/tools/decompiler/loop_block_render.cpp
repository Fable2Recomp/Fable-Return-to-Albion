#include "tools/decompiler/loop_block_render.h"

#include "tools/decompiler/rendered/rendered_lua_text.h"

namespace refii::lua {

bool RenderLuaLoopBodyRange(const LuaLoopBlockRenderContext& context,
                            int body_start,
                            int body_end,
                            int continue_target,
                            int exit_target,
                            std::string* error) {
    if (context.values == nullptr || context.declared == nullptr || !context.render_range) {
        if (error != nullptr) {
            *error = "loop_body_context_missing";
        }
        return false;
    }

    auto& values = *context.values;
    auto& declared = *context.declared;
    const std::vector<StraightLineValue> saved_values = values;
    const std::vector<bool> saved_declared = declared;
    const bool ok = body_start > body_end ||
                    context.render_range(body_start, body_end, continue_target, exit_target);
    values = saved_values;
    declared = saved_declared;
    return ok;
}

LuaLoopRenderResult FinalizeLuaEndClosedLoop(const LuaLoopBlockRenderContext& context,
                                             int invalidate_line,
                                             int finalize_continue_target,
                                             int finalize_exit_target,
                                             int next_current,
                                             int resolved_next_current,
                                             int outer_continue_target,
                                             int outer_exit_target,
                                             LuaLoopRenderDisposition default_disposition,
                                             bool emit_break_on_outer_exit) {
    if (context.lines == nullptr) {
        return LuaLoopRenderResult{.disposition = LuaLoopRenderDisposition::kError};
    }

    AppendIndentedStatement(context.lines, "  ", "end");
    if (invalidate_line >= 1 && context.invalidate_values) {
        context.invalidate_values(invalidate_line);
    }
    if (context.finalize_pending_jump &&
        context.finalize_pending_jump(finalize_continue_target, finalize_exit_target)) {
        return LuaLoopRenderResult{
            .disposition = LuaLoopRenderDisposition::kReturn,
            .next_current = -1,
        };
    }
    if (next_current == outer_continue_target || resolved_next_current == outer_continue_target) {
        return LuaLoopRenderResult{
            .disposition = LuaLoopRenderDisposition::kReturn,
            .next_current = -1,
        };
    }
    if (outer_exit_target != -1 &&
        (next_current == outer_exit_target || resolved_next_current == outer_exit_target)) {
        if (emit_break_on_outer_exit) {
            AppendIndentedStatement(context.lines, "  ", "break");
        }
        return LuaLoopRenderResult{
            .disposition = LuaLoopRenderDisposition::kReturn,
            .next_current = -1,
        };
    }
    if (default_disposition == LuaLoopRenderDisposition::kContinue) {
        return LuaLoopRenderResult{
            .disposition = LuaLoopRenderDisposition::kContinue,
            .next_current = next_current,
        };
    }
    if (default_disposition == LuaLoopRenderDisposition::kReturn) {
        return LuaLoopRenderResult{
            .disposition = LuaLoopRenderDisposition::kReturn,
            .next_current = -1,
        };
    }
    return LuaLoopRenderResult{
        .disposition = LuaLoopRenderDisposition::kNotHandled,
        .next_current = -1,
    };
}

}  // namespace refii::lua
