#include "tools/decompiler/conditional_block_render.h"

#include "tools/decompiler/rendered_lua_text.h"

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

LuaJumpRenderResult TryRenderLuaSharedTailConditionalBlock(
    const LuaConditionalBlockRenderContext& context,
    const decompiler::LuaSharedTailConditionalBlock& block,
    std::string_view condition,
    const LuaBranchStateSnapshot& saved_state,
    const LuaBranchStateSnapshot& then_entry_state,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.render_range || !context.collect_written_registers_in_range ||
        !context.invalidate_values || !context.finalize_pending_jump) {
        return ErrorResult(error, "shared_tail_block_context_missing");
    }

    auto& lines = *context.lines;
    auto& values = *context.values;
    auto& declared = *context.declared;
    const auto restore_state = [&](const LuaBranchStateSnapshot& state) {
        values = state.values;
        declared = state.declared;
    };

    AppendIndentedStatement(&lines, "  ", "if " + std::string(condition) + " then");
    restore_state(then_entry_state);
    if (block.then_start <= block.then_end &&
        !context.render_range(
            block.then_start, block.then_end, loop_continue_target, loop_exit_target)) {
        return ErrorResult(error, error == nullptr ? std::string() : *error);
    }
    const std::vector<StraightLineValue> then_end_values = values;
    const std::vector<bool> then_end_declared = declared;
    restore_state(saved_state);

    if (block.has_else) {
        AppendIndentedStatement(&lines, "  ", "else");
        if (block.else_start <= block.else_end &&
            !context.render_range(
                block.else_start, block.else_end, loop_continue_target, loop_exit_target)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        const std::vector<StraightLineValue> else_end_values = values;
        const std::vector<bool> else_end_declared = declared;
        restore_state(saved_state);
        AppendIndentedStatement(&lines, "  ", "end");
        context.invalidate_values(invalidate_line);
        const std::vector<bool> then_written_registers =
            context.collect_written_registers_in_range(block.then_start, block.then_end);
        const std::vector<bool> else_written_registers =
            context.collect_written_registers_in_range(block.else_start, block.else_end);
        const LuaMergedBranchState merged_state =
            BuildLuaMergedBranchState(saved_state,
                                      LuaBranchStateSnapshot{then_end_values, then_end_declared},
                                      then_written_registers,
                                      LuaBranchStateSnapshot{else_end_values, else_end_declared},
                                      else_written_registers,
                                      context.declarations,
                                      context.function,
                                      block.after_line,
                                      context.register_count);
        values = merged_state.values;
        declared = merged_state.declared;
        if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
            return ReturnResult();
        }
        return ContinueResult(block.after_line);
    }

    AppendIndentedStatement(&lines, "  ", "end");
    context.invalidate_values(invalidate_line);
    const std::vector<bool> then_written_registers =
        context.collect_written_registers_in_range(block.then_start, block.then_end);
    const LuaMergedBranchState merged_state =
        BuildLuaMergedBranchState(saved_state,
                                  LuaBranchStateSnapshot{then_end_values, then_end_declared},
                                  then_written_registers,
                                  saved_state,
                                  {},
                                  context.declarations,
                                  context.function,
                                  block.after_line,
                                  context.register_count);
    values = merged_state.values;
    declared = merged_state.declared;
    if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
        return ReturnResult();
    }
    return ContinueResult(block.after_line);
}

LuaJumpRenderResult TryRenderLuaConditionalBlock(
    const LuaConditionalBlockRenderContext& context,
    const decompiler::LuaConditionalBlock& block,
    std::string_view condition,
    const LuaBranchStateSnapshot& saved_state,
    const LuaBranchStateSnapshot& then_entry_state,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.render_range || !context.collect_written_registers_in_range ||
        !context.last_appended_statement_is_terminal || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return ErrorResult(error, "conditional_block_context_missing");
    }

    auto& lines = *context.lines;
    auto& values = *context.values;
    auto& declared = *context.declared;
    const auto restore_state = [&](const LuaBranchStateSnapshot& state) {
        values = state.values;
        declared = state.declared;
    };

    AppendIndentedStatement(&lines, "  ", "if " + std::string(condition) + " then");
    const int nested_then_continue_target =
        block.shared_continue_target != -1 ? block.shared_continue_target : loop_continue_target;
    const int nested_then_exit_target =
        block.shared_continue_target != -1 ? block.after_line : loop_exit_target;
    const std::size_t then_lines_start = lines.size();
    restore_state(then_entry_state);
    if (block.then_start <= block.then_end &&
        !context.render_range(
            block.then_start, block.then_end, nested_then_continue_target, nested_then_exit_target)) {
        return ErrorResult(error, error == nullptr ? std::string() : *error);
    }
    const std::vector<StraightLineValue> then_end_values = values;
    const std::vector<bool> then_end_declared = declared;
    restore_state(saved_state);
    if (block.then_exits_loop && !context.last_appended_statement_is_terminal(then_lines_start)) {
        AppendIndentedStatement(&lines, "  ", "break");
    }

    if (block.has_else) {
        AppendIndentedStatement(&lines, "  ", "else");
        const int nested_else_continue_target =
            block.shared_continue_target != -1 ? block.shared_continue_target
                                               : loop_continue_target;
        const int nested_else_exit_target =
            block.shared_continue_target != -1 ? block.after_line : loop_exit_target;
        if (block.else_start <= block.else_end &&
            !context.render_range(
                block.else_start, block.else_end, nested_else_continue_target, nested_else_exit_target)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        const std::vector<StraightLineValue> else_end_values = values;
        const std::vector<bool> else_end_declared = declared;
        restore_state(saved_state);
        AppendIndentedStatement(&lines, "  ", "end");
        context.invalidate_values(invalidate_line);
        if (block.shared_continue_target != -1) {
            return ContinueResult(block.after_line);
        }
        const std::vector<bool> then_written_registers =
            context.collect_written_registers_in_range(block.then_start, block.then_end);
        const std::vector<bool> else_written_registers =
            context.collect_written_registers_in_range(block.else_start, block.else_end);
        const LuaMergedBranchState merged_state =
            BuildLuaMergedBranchState(saved_state,
                                      LuaBranchStateSnapshot{then_end_values, then_end_declared},
                                      then_written_registers,
                                      LuaBranchStateSnapshot{else_end_values, else_end_declared},
                                      else_written_registers,
                                      context.declarations,
                                      context.function,
                                      block.after_line,
                                      context.register_count);
        values = merged_state.values;
        declared = merged_state.declared;
        if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
            return ReturnResult();
        }
        return ContinueResult(block.after_line);
    }

    AppendIndentedStatement(&lines, "  ", "end");
    context.invalidate_values(invalidate_line);
    const std::vector<bool> then_written_registers =
        context.collect_written_registers_in_range(block.then_start, block.then_end);
    const LuaMergedBranchState merged_state =
        BuildLuaMergedBranchState(saved_state,
                                  LuaBranchStateSnapshot{then_end_values, then_end_declared},
                                  then_written_registers,
                                  saved_state,
                                  {},
                                  context.declarations,
                                  context.function,
                                  block.after_line,
                                  context.register_count);
    values = merged_state.values;
    declared = merged_state.declared;
    if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
        return ReturnResult();
    }
    return ContinueResult(block.after_line);
}

}  // namespace refii::lua
