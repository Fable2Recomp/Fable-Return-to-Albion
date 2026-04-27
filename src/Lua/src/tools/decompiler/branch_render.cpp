#include "tools/decompiler/branch_render.h"

#include "tools/decompiler/branch_loop_render.h"
#include "tools/decompiler/branch_preview_render.h"
#include "tools/decompiler/branch_structured_render.h"
#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>
#include <cstddef>
#include <cctype>
#include <sstream>
#include <string>

namespace refii::lua {
namespace {

bool IsConditionalOpcode(Lua51Opcode opcode) {
    return decompiler::IsConditionalBranchOpcode(opcode);
}

bool IsLuaBranchRenderQuestParityPath(const Lua51Function& function) {
    return ClassifyLuaScriptProfile(function).Has(LuaScriptProfileFlag::kQuestHotspot);
}

bool IsLuaFunctionBoundaryReturnLine(const std::vector<Lua51Instruction>& instructions,
                                     int line,
                                     int target_reg) {
    if (line < 1 || static_cast<std::size_t>(line) > instructions.size()) {
        return false;
    }
    const Lua51Instruction& instruction =
        instructions[static_cast<std::size_t>(line - 1)];
    if (instruction.opcode != Lua51Opcode::kReturn || instruction.b == 1) {
        return false;
    }
    if (target_reg < 0) {
        return true;
    }
    if (instruction.b == 0) {
        return target_reg >= instruction.a;
    }
    return target_reg >= instruction.a &&
           target_reg < instruction.a + instruction.b - 1;
}

bool IsLuaBranchRenderFunctionBoundaryConditionalReturnShape(
    const Lua51Function& function,
    const std::vector<Lua51Instruction>& instructions,
    int branch_target,
    int join_line,
    int target_reg) {
    if (instructions.empty()) {
        return false;
    }
    constexpr std::size_t kMaxFunctionBoundaryParityInstructionCount = 1200;
    constexpr std::size_t kQuestBoundaryParityInstructionCount = 3200;
    const std::size_t max_instruction_count =
        IsLuaBranchRenderQuestParityPath(function)
            ? kQuestBoundaryParityInstructionCount
            : kMaxFunctionBoundaryParityInstructionCount;
    if (instructions.size() > max_instruction_count) {
        return false;
    }
    const int instruction_count = static_cast<int>(instructions.size());
    if (branch_target == instruction_count + 1 ||
        join_line == instruction_count + 1) {
        return true;
    }
    return IsLuaFunctionBoundaryReturnLine(instructions, branch_target, target_reg) ||
           IsLuaFunctionBoundaryReturnLine(instructions, join_line, target_reg) ||
           IsLuaFunctionBoundaryReturnLine(instructions, branch_target + 1, target_reg) ||
           IsLuaFunctionBoundaryReturnLine(instructions, join_line + 1, target_reg);
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

bool TryRenderLuaBranchConditionText(const LuaBranchRenderContext& context,
                                     int preview_start,
                                     int preview_end,
                                     int branch_target,
                                     int join_line,
                                     const Lua51Instruction& condition_instruction,
                                     int condition_line,
                                     const std::vector<StraightLineValue>& base_values,
                                     std::string* text,
                                     std::vector<StraightLineValue>* rendered_values,
                                     std::string* error) {
    if (text == nullptr || rendered_values == nullptr) {
        if (error != nullptr) {
            *error = "branch_condition_output_missing";
        }
        return false;
    }

    *rendered_values = base_values;
    if (preview_start <= preview_end) {
        if (!context.evaluate_inline_range) {
            if (error != nullptr) {
                *error = "branch_condition_preview_missing";
            }
            return false;
        }
        if (!context.evaluate_inline_range(
                preview_start, preview_end, rendered_values, error)) {
            return false;
        }
    }

    const int target_reg = ResolveLuaConditionalOwnershipRegisterForInstruction(
        condition_instruction, context.register_count);
    const bool prefer_function_boundary_parity =
        IsLuaBranchRenderFunctionBoundaryConditionalReturnShape(context.function,
                                                                context.instructions,
                                                                branch_target,
                                                                join_line,
                                                                target_reg);
    *text = prefer_function_boundary_parity
                ? RenderLuaConditionWithFunctionBoundaryParity(*rendered_values,
                                                               context.declarations,
                                                               context.function,
                                                               condition_instruction,
                                                               condition_line)
                : RenderLuaConditionWithValues(*rendered_values,
                                               context.declarations,
                                               context.function,
                                               condition_instruction,
                                               condition_line);
    if (text->empty()) {
        if (error != nullptr) {
            *error = "branch_condition_render_failed@" + std::to_string(condition_line);
        }
        return false;
    }
    return true;
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
    const auto trace = [&](std::string_view message) {
        if (context.trace_render && context.trace) {
            context.trace(message);
        }
    };
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
        if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
            AppendIndentedStatement(&lines, "  ", "end");
            return ReturnResult();
        }
        AppendIndentedStatement(&lines, "  ", "end");
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

    LuaConditionalRootCommitPacket root_commit_packet;
    const bool have_root_commit_packet =
        decompiler::IsConditionalBranchOpcode(instruction.opcode) &&
        TryResolveLuaConditionalRootCommitPacket(&context.control_flow_graph,
                                                 instructions,
                                                 instruction_count,
                                                 current,
                                                 std::max(current, branch_target - 2),
                                                 branch_target,
                                                 branch_target,
                                                 register_count,
                                                 branch_target,
                                                 branch_target,
                                                 &root_commit_packet,
                                                 &function);

    std::string condition;
    std::vector<StraightLineValue> preview_values_for_chain;
    const int root_join_line =
        have_root_commit_packet && root_commit_packet.effective_join_line > 0
            ? root_commit_packet.effective_join_line
            : branch_target;
    if (!TryRenderLuaBranchConditionText(context,
                                         context.range_start_line,
                                         current - 1,
                                         branch_target,
                                         root_join_line,
                                         instruction,
                                         line,
                                         values,
                                         &condition,
                                         &preview_values_for_chain,
                                         error)) {
        condition = context.render_condition(instruction, line);
        preview_values_for_chain = values;
    }
    if (condition.empty()) {
        return ErrorResult(error, "unsupported_condition_expression");
    }

    std::string combined_condition = condition;
    int combined_then_start = current + 2;
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
                                      &context.control_flow_graph,
                                      instructions,
                                      &preview_values_for_chain,
                                      declaration_map,
                                      function,
                                      context.evaluate_inline_range);
    }
    if (context.trace_render) {
        std::ostringstream stream;
        stream << "branch current=" << current << " target=" << branch_target
               << " condition=" << condition
               << " combined_condition=" << combined_condition
               << " combined_then_start=" << combined_then_start;
        trace(stream.str());
    }
    const bool has_combined_then_state = combined_then_start != current + 2;
    const auto combined_then_range_has_backward_jump = [&]() {
        if (branch_target <= current + 2 || branch_target - 1 > instruction_count) {
            return false;
        }
        return then_range_has_backward_jump(combined_then_start, branch_target - 1);
    };
    const auto trailing_then_jump_reaches_loop_continue = [&]() {
        if (loop_continue_target == -1 ||
            branch_target <= combined_then_start ||
            branch_target - 1 < 1 ||
            branch_target - 1 > instruction_count ||
            branch_target > end_line) {
            return false;
        }

        const Lua51Instruction& trailing_then_jump =
            instructions[static_cast<std::size_t>(branch_target - 2)];
        if (trailing_then_jump.opcode != Lua51Opcode::kJmp) {
            return false;
        }

        const int trailing_then_target =
            compute_jump_target(branch_target - 1, trailing_then_jump.sbx);
        const int resolved_trailing_then_target =
            decompiler::ResolveTerminalJumpTarget(instructions, trailing_then_target);
        return trailing_then_target == loop_continue_target ||
               resolved_trailing_then_target == loop_continue_target;
    };
    const bool skip_combined_preview_join_shortcuts =
        skip_preview_join_shortcuts || combined_then_range_has_backward_jump();
    const bool skip_preview_shortcuts_for_loop_continue_tail =
        trailing_then_jump_reaches_loop_continue();

    if (!skip_preview_shortcuts_for_loop_continue_tail) {
        const LuaBranchPreviewShortcutContext preview_shortcut_context{
            .lines = &lines,
            .values = &values,
            .declared = &declared,
            .control_flow_graph = context.control_flow_graph,
            .instructions = instructions,
            .preview_context = context.preview_context,
            .precomputed_root_packet =
                have_root_commit_packet ? &root_commit_packet : nullptr,
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
        .precomputed_root_packet =
            have_root_commit_packet ? &root_commit_packet : nullptr,
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
