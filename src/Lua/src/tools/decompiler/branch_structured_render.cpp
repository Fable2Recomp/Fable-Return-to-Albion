#include "tools/decompiler/branch_structured_render.h"

#include "tools/decompiler/branch_loop_render.h"

namespace refii::lua {
namespace {

LuaJumpRenderResult ErrorResult(std::string* error, std::string message) {
    if (error != nullptr) {
        *error = std::move(message);
    }
    return {};
}

}  // namespace

LuaJumpRenderResult TryRenderLuaStructuredConditionalBranch(
    const LuaStructuredConditionalBranchContext& context,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    int combined_then_start,
    std::string_view condition,
    std::string_view combined_condition,
    const std::vector<StraightLineValue>& then_entry_values,
    int loop_continue_target,
    int loop_exit_target,
    int invalidate_line,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.render_range || !context.collect_written_registers_in_range ||
        !context.last_appended_statement_is_terminal || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return ErrorResult(error, "structured_branch_context_missing");
    }

    auto& values = *context.values;
    auto& declared = *context.declared;
    const auto restore_state = [&](const LuaBranchStateSnapshot& state) {
        values = state.values;
        declared = state.declared;
    };

    const LuaBranchLoopRenderContext branch_loop_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .render_range = context.render_range,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    const auto compute_jump_target = [&](int pc, int sbx) {
        return decompiler::ComputeJumpTargetLine(pc, sbx, instruction_count);
    };

    decompiler::LuaBackJumpLoopBlock back_jump_loop_block;
    if (decompiler::TryBuildLuaBackJumpLoopBlock(context.instructions,
                                                 current,
                                                 branch_target,
                                                 end_line,
                                                 &back_jump_loop_block)) {
        return TryRenderLuaBackJumpLoopBranch(
            branch_loop_context, back_jump_loop_block, condition, invalidate_line, error);
    }

    if (branch_target - 1 >= current + 2 &&
        branch_target - 1 <= end_line &&
        branch_target - 1 <= instruction_count) {
        const Lua51Instruction& trailing_loop_jump =
            context.instructions[static_cast<std::size_t>(branch_target - 2)];
        if (trailing_loop_jump.opcode == Lua51Opcode::kJmp) {
            const int preamble_loop_target =
                compute_jump_target(branch_target - 1, trailing_loop_jump.sbx);
            if (preamble_loop_target >= 1 && preamble_loop_target < current) {
                const LuaBranchStateSnapshot saved_state{values, declared};
                const LuaBranchStateSnapshot then_entry_state{then_entry_values, declared};
                restore_state(then_entry_state);
                const LuaJumpRenderResult render_result =
                    TryRenderLuaPreambleLoopBranch(branch_loop_context,
                                                   combined_condition,
                                                   current + 2,
                                                   branch_target - 2,
                                                   preamble_loop_target,
                                                   branch_target,
                                                   invalidate_line,
                                                   branch_target,
                                                   error);
                restore_state(saved_state);
                if (render_result.disposition != LuaJumpRenderDisposition::kError) {
                    return render_result;
                }
            }
        }
    }

    decompiler::LuaConditionalBlock conditional_block;
    if (!decompiler::TryBuildLuaConditionalBlock(context.control_flow_graph,
                                                 context.instructions,
                                                 current,
                                                 combined_then_start,
                                                 branch_target,
                                                 end_line,
                                                 loop_exit_target,
                                                 &conditional_block)) {
        return ErrorResult(error, "conditional_block_shape_failed");
    }

    const LuaBranchStateSnapshot saved_state{values, declared};
    const LuaBranchStateSnapshot then_entry_state{then_entry_values, declared};
    const LuaConditionalBlockRenderContext conditional_block_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .render_range = context.render_range,
        .collect_written_registers_in_range = context.collect_written_registers_in_range,
        .last_appended_statement_is_terminal = context.last_appended_statement_is_terminal,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    return TryRenderLuaConditionalBlock(conditional_block_context,
                                        conditional_block,
                                        combined_condition,
                                        saved_state,
                                        then_entry_state,
                                        invalidate_line,
                                        loop_continue_target,
                                        loop_exit_target,
                                        error);
}

}  // namespace refii::lua
