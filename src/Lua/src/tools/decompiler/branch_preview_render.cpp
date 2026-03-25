#include "tools/decompiler/branch_preview_render.h"

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/preview_join_render.h"
#include "tools/decompiler/testset_render.h"

namespace refii::lua {
namespace {

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

bool TryApplyLuaConditionalPreviewFallback(const LuaBranchPreviewFallbackContext& context,
                                           const Lua51Instruction& instruction,
                                           int current,
                                           int branch_target,
                                           int end_line,
                                           int instruction_count,
                                           int line,
                                           std::vector<StraightLineValue>* values,
                                           const std::vector<bool>* declared,
                                           int* next_current) {
    if (values == nullptr || next_current == nullptr ||
        !context.evaluate_inline_range || !context.preview_range_is_side_effect_free) {
        return false;
    }

    const bool valid_fallback_range =
        branch_target > current + 2 &&
        branch_target <= end_line &&
        branch_target <= instruction_count &&
        context.preview_range_is_side_effect_free(current + 2, branch_target - 1);
    if (!valid_fallback_range) {
        return false;
    }

    if (instruction.opcode == Lua51Opcode::kTest && instruction.c != 0) {
        const LuaPreviewFallbackContext preview_fallback_context{
            .declarations = context.declarations,
            .function = context.function,
            .register_count = context.register_count,
            .preview_context = context.preview_context,
            .initial_declared = declared,
            .evaluate_inline_range = context.evaluate_inline_range,
        };
        StraightLineValue merged_value;
        if (!TryMergeLuaPreviewRangeFallback(preview_fallback_context,
                                             current + 2,
                                             branch_target - 1,
                                             line,
                                             *values,
                                             instruction.a,
                                             true,
                                             &merged_value)) {
            return false;
        }
        (*values)[static_cast<std::size_t>(instruction.a)] = std::move(merged_value);
        *next_current = branch_target;
        return true;
    }

    if (instruction.opcode == Lua51Opcode::kTestSet &&
        instruction.c != 0 &&
        instruction.a >= 0 &&
        instruction.b >= 0) {
        const LuaTestSetFallbackContext testset_context{
            .preview_fallback =
                {
                    .declarations = context.declarations,
                    .function = context.function,
                    .register_count = context.register_count,
                    .preview_context = context.preview_context,
                    .initial_declared = declared,
                    .evaluate_inline_range = context.evaluate_inline_range,
                },
        };
        const decompiler::LuaTestSetLoopControlBlock testset_block{
            .branch_target = branch_target,
            .fallback_start = current + 2,
            .fallback_end = branch_target - 1,
            .target_register = instruction.a,
            .source_register = instruction.b,
            .invert_condition = instruction.c != 0,
        };
        StraightLineValue merged_value;
        if (!TryMergeLuaTestSetFallbackValues(
                testset_context, testset_block, line, *values, &merged_value)) {
            return false;
        }
        (*values)[static_cast<std::size_t>(instruction.a)] = std::move(merged_value);
        *next_current = branch_target;
        return true;
    }

    return false;
}

LuaJumpRenderResult TryRenderLuaSharedTailPreviewConditional(
    const LuaBranchPreviewSharedTailContext& context,
    int branch_target,
    int combined_then_start,
    int end_line,
    std::string_view combined_condition,
    const std::vector<StraightLineValue>& then_entry_values,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.evaluate_inline_range || !context.render_range ||
        !context.collect_written_registers_in_range ||
        !context.last_appended_statement_is_terminal || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return {};
    }

    decompiler::LuaSharedTailConditionalBlock shared_tail_block;
    if (!decompiler::TryBuildLuaSharedTailConditionalBlock(
            context.instructions,
            branch_target,
            combined_then_start,
            end_line,
            &shared_tail_block)) {
        return {};
    }

    const Lua51Instruction& inner_condition_instruction =
        context.instructions[static_cast<std::size_t>(shared_tail_block.inner_condition_line - 1)];
    std::vector<StraightLineValue> inner_preview_values = then_entry_values;
    std::string inner_preview_error;
    if (combined_then_start <= shared_tail_block.inner_condition_line - 1 &&
        !context.evaluate_inline_range(combined_then_start,
                                       shared_tail_block.inner_condition_line - 1,
                                       &inner_preview_values,
                                       &inner_preview_error)) {
        return {};
    }

    const int inner_render_line =
        static_cast<int>(inner_condition_instruction.line == 0
                             ? shared_tail_block.inner_condition_line
                             : inner_condition_instruction.line);
    const std::string inner_condition =
        RenderLuaConditionWithValues(inner_preview_values,
                                     context.declarations,
                                     context.function,
                                     inner_condition_instruction,
                                     inner_render_line);
    if (inner_condition.empty()) {
        return {};
    }

    const std::string or_condition = RenderLogicalExpression(
        RenderUnaryExpression("not", std::string(combined_condition)), "or", inner_condition);
    const LuaBranchStateSnapshot saved_state{*context.values, *context.declared};
    const LuaBranchStateSnapshot then_entry_state{then_entry_values, *context.declared};
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
    return TryRenderLuaSharedTailConditionalBlock(conditional_block_context,
                                                  shared_tail_block,
                                                  or_condition,
                                                  saved_state,
                                                  then_entry_state,
                                                  invalidate_line,
                                                  loop_continue_target,
                                                  loop_exit_target,
                                                  error);
}

LuaJumpRenderResult TryApplyLuaConditionalPreviewShortcuts(
    const LuaBranchPreviewShortcutContext& context,
    const Lua51Instruction& instruction,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    std::string_view condition,
    std::string_view combined_condition,
    int combined_then_start,
    const std::vector<StraightLineValue>& then_entry_values,
    bool skip_combined_preview_join_shortcuts,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        context.preview_context == nullptr || !context.evaluate_inline_range ||
        !context.preview_range_is_side_effect_free || !context.render_range ||
        !context.collect_written_registers_in_range ||
        !context.last_appended_statement_is_terminal || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return {};
    }

    const LuaBranchPreviewFallbackContext preview_fallback_context{
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .preview_context = context.preview_context,
        .evaluate_inline_range = context.evaluate_inline_range,
        .preview_range_is_side_effect_free = context.preview_range_is_side_effect_free,
    };
    int fallback_next_current = -1;
    if (TryApplyLuaConditionalPreviewFallback(preview_fallback_context,
                                              instruction,
                                              current,
                                              branch_target,
                                              end_line,
                                              instruction_count,
                                              invalidate_line,
                                              context.values,
                                              context.declared,
                                              &fallback_next_current)) {
        return ContinueResult(fallback_next_current);
    }

    if (skip_combined_preview_join_shortcuts) {
        return {};
    }

    const LuaJumpRenderResult join_result =
        TryApplyLuaPreviewConditionalJoinShortcuts(context.lines,
                                                   context.control_flow_graph,
                                                   context.instructions,
                                                   *context.preview_context,
                                                   context.values,
                                                   context.declared,
                                                   condition,
                                                   current,
                                                   branch_target,
                                                   end_line,
                                                   instruction_count,
                                                   context.register_count,
                                                   context.preview_range_is_side_effect_free,
                                                   context.invalidate_values,
                                                   invalidate_line);
    if (join_result.disposition != LuaJumpRenderDisposition::kError) {
        return join_result;
    }

    const LuaBranchPreviewSharedTailContext shared_tail_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .evaluate_inline_range = context.evaluate_inline_range,
        .render_range = context.render_range,
        .collect_written_registers_in_range = context.collect_written_registers_in_range,
        .last_appended_statement_is_terminal = context.last_appended_statement_is_terminal,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    const LuaJumpRenderResult render_result =
        TryRenderLuaSharedTailPreviewConditional(shared_tail_context,
                                                 branch_target,
                                                 combined_then_start,
                                                 end_line,
                                                 combined_condition,
                                                 then_entry_values,
                                                 invalidate_line,
                                                 loop_continue_target,
                                                 loop_exit_target,
                                                 error);
    if (render_result.disposition != LuaJumpRenderDisposition::kError) {
        return render_result;
    }

    const LuaJumpRenderResult call_join_result =
        TryApplyLuaPreviewCallJoinShortcut(context.lines,
                                           context.instructions,
                                           *context.preview_context,
                                           context.values,
                                           context.declared,
                                           condition,
                                           current,
                                           branch_target,
                                           end_line,
                                           instruction_count,
                                           context.register_count,
                                           context.invalidate_values);
    if (call_join_result.disposition != LuaJumpRenderDisposition::kError) {
        if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
            return ReturnResult();
        }
        return call_join_result;
    }

    return {};
}

}  // namespace refii::lua
