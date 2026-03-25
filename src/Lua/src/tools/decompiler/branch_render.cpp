#include "tools/decompiler/branch_render.h"

#include "tools/decompiler/branch_loop_render.h"
#include "tools/decompiler/branch_preview_render.h"
#include "tools/decompiler/branch_structured_render.h"
#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>
#include <cstddef>
#include <sstream>

namespace refii::lua {
namespace {

bool IsConditionalOpcode(Lua51Opcode opcode) {
    return decompiler::IsConditionalBranchOpcode(opcode);
}

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

LuaJumpRenderResult TryRenderLuaConditionalControlFlow(const LuaBranchRenderContext& context,
                                                       int current,
                                                       int end_line,
                                                       int loop_continue_target,
                                                       int loop_exit_target,
                                                       int line,
                                                       std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        context.preview_context == nullptr ||
        !context.evaluate_inline_range || !context.evaluate_branch_path_to_join ||
        !context.preview_range_is_side_effect_free ||
        !context.render_condition || !context.render_range ||
        !context.collect_written_registers_in_range ||
        !context.last_appended_statement_is_terminal || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return ErrorResult(error, "branch_context_missing");
    }

    auto& lines = *context.lines;
    auto& values = *context.values;
    auto& declared = *context.declared;
    const auto& instructions = context.instructions;
    const auto& declaration_map = context.declarations;
    const auto& function = context.function;
    const int register_count = context.register_count;
    const int instruction_count = static_cast<int>(instructions.size());
    const LuaBranchLoopRenderContext branch_loop_context{
        .lines = &lines,
        .values = &values,
        .declared = &declared,
        .render_range = context.render_range,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    const auto restore_state = [&](const LuaBranchStateSnapshot& state) {
        values = state.values;
        declared = state.declared;
    };

    if (current < 1 || static_cast<std::size_t>(current + 1) > instructions.size()) {
        return ErrorResult(error, "conditional_pc_out_of_range");
    }

    const Lua51Instruction& instruction = instructions[static_cast<std::size_t>(current - 1)];
    const Lua51Instruction& jump = instructions[static_cast<std::size_t>(current)];
    if (!IsConditionalOpcode(instruction.opcode) || jump.opcode != Lua51Opcode::kJmp) {
        return ErrorResult(error, "conditional_shape_mismatch");
    }

    const auto compute_jump_target = [&](int pc, int sbx) {
        return decompiler::ComputeJumpTargetLine(pc, sbx, instruction_count);
    };

    const int branch_target =
        compute_jump_target(current + 1, jump.sbx);
    const int resolved_branch_target =
        decompiler::ResolveTerminalJumpTarget(instructions, branch_target);

    int backward_loop_back_line = -1;
    if (branch_target < current + 2) {
        for (int candidate = end_line; candidate >= current + 2; --candidate) {
            const Lua51Instruction& maybe_loop_back =
                instructions[static_cast<std::size_t>(candidate - 1)];
            if (maybe_loop_back.opcode != Lua51Opcode::kJmp) {
                continue;
            }

            const int maybe_loop_target =
                compute_jump_target(candidate, maybe_loop_back.sbx);
            if (maybe_loop_target == current) {
                backward_loop_back_line = candidate;
                break;
            }
        }
    }
    if (backward_loop_back_line != -1 &&
        (branch_target == loop_continue_target ||
         resolved_branch_target == loop_continue_target)) {
        const std::string loop_condition = context.render_condition(instruction, line);
        if (loop_condition.empty()) {
            return ErrorResult(error, "unsupported_condition_expression");
        }
        const decompiler::LuaBackJumpLoopBlock block{
            .body_start = current + 2,
            .body_end = backward_loop_back_line - 1,
            .continue_target = current,
            .exit_target = branch_target,
            .after_line = -1,
        };
        const LuaJumpRenderResult render_result =
            TryRenderLuaBackJumpLoopBranch(
                branch_loop_context, block, loop_condition, line, error);
        if (render_result.disposition != LuaJumpRenderDisposition::kError) {
            return ReturnResult();
        }
    }
    if (loop_continue_target != -1 &&
        (branch_target == loop_continue_target ||
         resolved_branch_target == loop_continue_target)) {
        const std::string condition = context.render_condition(instruction, line);
        if (condition.empty()) {
            return ErrorResult(error, "unsupported_condition_expression");
        }

        AppendIndentedStatement(&lines, "  ", RenderIfStatementHeader(condition));
        const LuaBranchStateSnapshot saved_state{values, declared};
        if (current + 2 <= end_line &&
            !context.render_range(current + 2,
                                  end_line,
                                  loop_continue_target,
                                  loop_exit_target)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        restore_state(saved_state);
        AppendIndentedStatement(&lines, "  ", "end");
        if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
            return ReturnResult();
        }
        return ReturnResult();
    }
    if (loop_exit_target != -1 &&
        (branch_target == loop_exit_target ||
         resolved_branch_target == loop_exit_target)) {
        const std::string condition = context.render_condition(instruction, line);
        if (condition.empty()) {
            return ErrorResult(error, "unsupported_condition_expression");
        }

        AppendIndentedStatement(
            &lines, "  ", RenderIfStatementHeader(RenderUnaryExpression("not", condition)));
        AppendIndentedStatement(&lines, "  ", "break");
        AppendIndentedStatement(&lines, "  ", "end");
        const LuaBranchStateSnapshot saved_state{values, declared};
        if (current + 2 <= end_line &&
            !context.render_range(current + 2,
                                  end_line,
                                  loop_continue_target,
                                  loop_exit_target)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        restore_state(saved_state);
        if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
            return ReturnResult();
        }
        return ReturnResult();
    }
    if (branch_target < current + 2 || branch_target > instruction_count + 1) {
        return ErrorResult(error,
                           "unsupported_branch_target@" + std::to_string(current) +
                               "->" + std::to_string(branch_target) +
                               "(continue=" + std::to_string(loop_continue_target) +
                               ",exit=" + std::to_string(loop_exit_target) + ")");
    }

    const std::string condition = context.render_condition(instruction, line);
    if (condition.empty()) {
        return ErrorResult(error, "unsupported_condition_expression");
    }

    std::string combined_condition = condition;
    int combined_then_start = current + 2;
    std::vector<StraightLineValue> preview_values_for_chain = values;
    const auto then_range_has_backward_jump = [&](int start_line, int finish_line) {
        if (finish_line < start_line || finish_line > instruction_count) {
            return false;
        }
        for (int candidate = start_line; candidate <= finish_line; ++candidate) {
            const Lua51Instruction& candidate_instruction =
                instructions[static_cast<std::size_t>(candidate - 1)];
            if (candidate_instruction.opcode != Lua51Opcode::kJmp) {
                continue;
            }
            if (compute_jump_target(candidate, candidate_instruction.sbx) <= candidate) {
                return true;
            }
        }
        return false;
    };
    const bool skip_preview_join_shortcuts =
        then_range_has_backward_jump(current + 2, branch_target - 1);
    if (!skip_preview_join_shortcuts) {
        AppendLuaChainedAndConditions(&combined_condition,
                                      &combined_then_start,
                                      branch_target,
                                      instructions,
                                      &preview_values_for_chain,
                                      declaration_map,
                                      function,
                                      context.evaluate_inline_range);
    }
    const bool has_combined_then_state = combined_then_start != current + 2;
    const auto combined_then_range_has_backward_jump = [&]() {
        if (branch_target <= current + 2 || branch_target - 1 > instruction_count) {
            return false;
        }
        return then_range_has_backward_jump(combined_then_start, branch_target - 1);
    };
    const bool skip_combined_preview_join_shortcuts =
        skip_preview_join_shortcuts || combined_then_range_has_backward_jump();

    {
        const LuaBranchPreviewShortcutContext preview_shortcut_context{
            .lines = &lines,
            .values = &values,
            .declared = &declared,
            .control_flow_graph = context.control_flow_graph,
            .instructions = instructions,
            .preview_context = context.preview_context,
            .declarations = declaration_map,
            .function = function,
            .register_count = register_count,
            .evaluate_inline_range = context.evaluate_inline_range,
            .preview_range_is_side_effect_free = context.preview_range_is_side_effect_free,
            .render_range = context.render_range,
            .collect_written_registers_in_range =
                context.collect_written_registers_in_range,
            .last_appended_statement_is_terminal =
                context.last_appended_statement_is_terminal,
            .invalidate_values = context.invalidate_values,
            .finalize_pending_jump = context.finalize_pending_jump,
        };
        const LuaJumpRenderResult preview_result =
            TryApplyLuaConditionalPreviewShortcuts(preview_shortcut_context,
                                                   instruction,
                                                   current,
                                                   branch_target,
                                                   end_line,
                                                   instruction_count,
                                                   condition,
                                                   combined_condition,
                                                   combined_then_start,
                                                   has_combined_then_state
                                                       ? preview_values_for_chain
                                                       : values,
                                                   skip_combined_preview_join_shortcuts,
                                                   line,
                                                   loop_continue_target,
                                                   loop_exit_target,
                                                   error);
        if (preview_result.disposition != LuaJumpRenderDisposition::kError) {
            return preview_result;
        }
    }

    const LuaStructuredConditionalBranchContext structured_branch_context{
        .control_flow_graph = context.control_flow_graph,
        .instructions = instructions,
        .lines = &lines,
        .values = &values,
        .declared = &declared,
        .declarations = declaration_map,
        .function = function,
        .register_count = register_count,
        .render_range = context.render_range,
        .collect_written_registers_in_range =
            context.collect_written_registers_in_range,
        .last_appended_statement_is_terminal =
            context.last_appended_statement_is_terminal,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    return TryRenderLuaStructuredConditionalBranch(
        structured_branch_context,
        current,
        branch_target,
        end_line,
        instruction_count,
        combined_then_start,
        condition,
        combined_condition,
        has_combined_then_state ? preview_values_for_chain : values,
        loop_continue_target,
        loop_exit_target,
        line,
        error);
}

}  // namespace refii::lua
