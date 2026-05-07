#include "tools/decompiler/branch_loop_render.h"

#include "tools/decompiler/loop_block_render.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"

namespace refii::lua {
namespace {

LuaJumpRenderResult ErrorResult(std::string* error, std::string message) {
    if (error != nullptr) {
        *error = std::move(message);
    }
    return {};
}

LuaJumpRenderResult ContinueResult(int next_current) {
    return LuaJumpRenderResult{
        .disposition = LuaJumpRenderDisposition::kContinue,
        .next_current = next_current,
    };
}

LuaJumpRenderResult ReturnResult() {
    return LuaJumpRenderResult{
        .disposition = LuaJumpRenderDisposition::kReturn,
        .next_current = -1,
    };
}

}  // namespace

LuaJumpRenderResult TryRenderLuaBackJumpLoopBranch(
    const LuaBranchLoopRenderContext& context,
    const decompiler::LuaBackJumpLoopBlock& block,
    std::string_view condition,
    int invalidate_line,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.render_range || !context.invalidate_values || !context.finalize_pending_jump) {
        return ErrorResult(error, "branch_loop_context_missing");
    }

    auto& lines = *context.lines;
    const LuaLoopBlockRenderContext block_render_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .render_range = context.render_range,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };

    AppendIndentedStatement(&lines, "  ", RenderWhileStatementHeader(std::string(condition)));
    if (!RenderLuaLoopBodyRange(block_render_context,
                                block.body_start,
                                block.body_end,
                                block.continue_target,
                                block.exit_target,
                                error)) {
        return ErrorResult(error, error == nullptr ? std::string() : *error);
    }
    AppendIndentedStatement(&lines, "  ", "end");
    context.invalidate_values(invalidate_line);
    if (context.finalize_pending_jump(block.continue_target, block.exit_target)) {
        return ReturnResult();
    }
    return ContinueResult(block.after_line);
}

LuaJumpRenderResult TryRenderLuaPreambleLoopBranch(const LuaBranchLoopRenderContext& context,
                                                   std::string_view condition,
                                                   int body_start,
                                                   int body_end,
                                                   int continue_target,
                                                   int exit_target,
                                                   int invalidate_line,
                                                   int next_current,
                                                   std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.render_range || !context.invalidate_values || !context.finalize_pending_jump) {
        return ErrorResult(error, "branch_loop_context_missing");
    }

    auto& lines = *context.lines;
    const LuaLoopBlockRenderContext block_render_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .render_range = context.render_range,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };

    AppendIndentedStatement(&lines, "  ", RenderWhileStatementHeader(std::string(condition)));
    if (!RenderLuaLoopBodyRange(block_render_context,
                                body_start,
                                body_end,
                                continue_target,
                                exit_target,
                                error)) {
        return ErrorResult(error, error == nullptr ? std::string() : *error);
    }
    AppendIndentedStatement(&lines, "  ", "end");
    context.invalidate_values(invalidate_line);
    if (context.finalize_pending_jump(continue_target, exit_target)) {
        return ReturnResult();
    }
    return ContinueResult(next_current);
}

}  // namespace refii::lua
