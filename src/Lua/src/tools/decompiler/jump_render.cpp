#include "tools/decompiler/jump_render.h"

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

bool TryRenderLuaJumpReturn(std::vector<std::string>* lines,
                            const std::vector<Lua51Instruction>& instructions,
                            const LuaReturnValuesRenderer& return_values_for,
                            int jump_source_line,
                            int jump_target_line,
                            const std::vector<StraightLineValue>& state_values) {
    if (lines == nullptr || !return_values_for || jump_target_line < 1 ||
        static_cast<std::size_t>(jump_target_line) > instructions.size()) {
        return false;
    }

    const Lua51Instruction& target_instruction =
        instructions[static_cast<std::size_t>(jump_target_line - 1)];
    if (target_instruction.opcode != Lua51Opcode::kReturn) {
        return false;
    }

    const int line = static_cast<int>(
        target_instruction.line == 0 ? jump_source_line : target_instruction.line);
    const std::vector<StraightLineValue> results =
        return_values_for(state_values, target_instruction.a, target_instruction.b, line);
    AppendIndentedStatement(
        lines, "  ", RenderReturnStatement(JoinExpressions(results), !results.empty()));
    return true;
}

}  // namespace

LuaLoopTargetScope::LuaLoopTargetScope(std::vector<std::pair<int, int>>& stack_ref,
                                       int continue_target,
                                       int exit_target)
    : stack(stack_ref), active(continue_target != -1 || exit_target != -1) {
    if (active) {
        stack.emplace_back(continue_target, exit_target);
    }
}

LuaLoopTargetScope::~LuaLoopTargetScope() {
    if (active) {
        stack.pop_back();
    }
}

int FindAncestorLuaLoopTarget(const std::vector<std::pair<int, int>>& loop_target_stack,
                              int target,
                              int current_continue_target,
                              int current_exit_target) {
    bool skipped_current = false;
    for (auto it = loop_target_stack.rbegin(); it != loop_target_stack.rend(); ++it) {
        if (!skipped_current && it->first == current_continue_target &&
            it->second == current_exit_target) {
            skipped_current = true;
            continue;
        }
        if (target == it->first || (it->second != -1 && target == it->second)) {
            return target;
        }
    }
    return -1;
}

bool FinalizePendingLuaJump(std::vector<std::string>* lines,
                            int* pending_jump_target,
                            int current_continue_target,
                            int current_exit_target) {
    if (pending_jump_target == nullptr || *pending_jump_target == -1) {
        return false;
    }
    if (*pending_jump_target == current_continue_target) {
        *pending_jump_target = -1;
        return true;
    }
    if (current_exit_target != -1 && *pending_jump_target == current_exit_target) {
        AppendIndentedStatement(lines, "  ", "break");
        *pending_jump_target = -1;
        return true;
    }
    return true;
}

LuaJumpRenderResult TryRenderLuaJumpControlFlow(const LuaJumpRenderContext& context,
                                                int current,
                                                int end_line,
                                                int loop_continue_target,
                                                int loop_exit_target,
                                                int line,
                                                std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        context.pending_jump_target == nullptr || context.loop_target_stack == nullptr ||
        !context.call_arguments_for || !context.return_values_for ||
        !context.evaluate_inline_range || !context.render_range ||
        !context.render_condition || !context.invalidate_values) {
        return ErrorResult(error, "jump_context_missing");
    }

    const auto trace = [&](std::string_view message) {
        if (context.trace_render && context.trace) {
            context.trace(message);
        }
    };

    auto& lines = *context.lines;
    auto& values = *context.values;
    auto& declared = *context.declared;
    int& pending_jump_target = *context.pending_jump_target;
    const auto& loop_target_stack = *context.loop_target_stack;
    const auto& instructions = context.instructions;
    const auto& declaration_map = context.declarations;
    const auto& function = context.function;
    const int register_count = context.register_count;
    const int instruction_count = static_cast<int>(instructions.size());

    if (current < 1 || static_cast<std::size_t>(current) > instructions.size()) {
        return ErrorResult(error, "invalid_jump_pc");
    }

    const Lua51Instruction& instruction = instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kJmp) {
        return ErrorResult(error, "jump_opcode_expected");
    }

    const auto compute_jump_target = [&](int pc, int sbx) {
        return decompiler::ComputeJumpTargetLine(pc, sbx, instruction_count);
    };
    const auto resolve_terminal_jump_target = [&](int target_line) {
        return decompiler::ResolveTerminalJumpTarget(instructions, target_line);
    };
    const auto try_render_jump_return =
        [&](int jump_source_line,
            int jump_target_line,
            const std::vector<StraightLineValue>& state_values) {
            return TryRenderLuaJumpReturn(&lines,
                                          instructions,
                                          context.return_values_for,
                                          jump_source_line,
                                          jump_target_line,
                                          state_values);
        };

    const int jump_target = compute_jump_target(current, instruction.sbx);
    const int resolved_jump_target = resolve_terminal_jump_target(jump_target);
    if (jump_target == current + 1) {
        return ContinueResult(jump_target);
    }
    if (jump_target > end_line && end_line < instruction_count) {
        return ReturnResult();
    }
    if (loop_continue_target != -1 && jump_target == loop_continue_target) {
        return ReturnResult();
    }
    if (loop_exit_target != -1 && jump_target == loop_exit_target) {
        AppendIndentedStatement(&lines, "  ", "break");
        return ReturnResult();
    }
    if (jump_target == current + 2 && current + 1 <= instruction_count &&
        instructions[static_cast<std::size_t>(current)].opcode == Lua51Opcode::kClose) {
        return ContinueResult(jump_target);
    }
    if (jump_target == current + 2 && current + 1 <= instruction_count &&
        instructions[static_cast<std::size_t>(current)].opcode == Lua51Opcode::kJmp) {
        const Lua51Instruction& skipped_jump = instructions[static_cast<std::size_t>(current)];
        const int skipped_target = compute_jump_target(current + 1, skipped_jump.sbx);
        const int resolved_skipped_target = resolve_terminal_jump_target(skipped_target);
        if ((loop_continue_target != -1 &&
             (skipped_target == loop_continue_target ||
              resolved_skipped_target == loop_continue_target)) ||
            (loop_exit_target != -1 &&
             (skipped_target == loop_exit_target ||
              resolved_skipped_target == loop_exit_target)) ||
            try_render_jump_return(current, resolved_skipped_target, values)) {
            return ContinueResult(jump_target);
        }
    }
    if (current > 1 && jump_target > current + 1 &&
        jump_target + 1 <= instruction_count) {
        const Lua51Instruction& previous_instruction =
            instructions[static_cast<std::size_t>(current - 2)];
        const Lua51Instruction& tfor =
            instructions[static_cast<std::size_t>(jump_target - 1)];
        const Lua51Instruction& loop_back =
            instructions[static_cast<std::size_t>(jump_target)];
        const int body_start = current + 1;
        const int loop_exit = jump_target + 2;
        if (tfor.opcode == Lua51Opcode::kTForLoop &&
            loop_back.opcode == Lua51Opcode::kJmp &&
            compute_jump_target(jump_target + 1, loop_back.sbx) == body_start) {
            const int call_render_line = static_cast<int>(
                previous_instruction.line == 0 ? current - 1 : previous_instruction.line);
            std::string iterator_expression;
            bool recovered_iterator_expression = false;
            const auto try_recover_direct_iterator_tuple = [&]() {
                const StraightLineValue iterator_function = GetRegisterValue(values,
                                                                            declaration_map,
                                                                            function,
                                                                            tfor.a,
                                                                            line);
                const StraightLineValue iterator_state = GetRegisterValue(values,
                                                                         declaration_map,
                                                                         function,
                                                                         tfor.a + 1,
                                                                         line);
                const StraightLineValue iterator_control = GetRegisterValue(values,
                                                                           declaration_map,
                                                                           function,
                                                                           tfor.a + 2,
                                                                           line);
                if (!iterator_function.valid || !iterator_state.valid ||
                    !iterator_control.valid) {
                    return false;
                }

                iterator_expression = iterator_function.text + ", " +
                                      iterator_state.text + ", " +
                                      iterator_control.text;
                return true;
            };

            if (previous_instruction.opcode == Lua51Opcode::kCall &&
                previous_instruction.c == 4 &&
                tfor.a == previous_instruction.a) {
                std::vector<StraightLineValue> preview_values(
                    static_cast<std::size_t>(register_count));
                const int preview_seed_line = std::max(1, current - 3);
                for (int reg = 0; reg < register_count; ++reg) {
                    if (reg < function.parameter_count ||
                        declaration_map.Get(reg, preview_seed_line) != nullptr) {
                        preview_values[static_cast<std::size_t>(reg)] =
                            StraightLineValue{
                                GetStraightLineRegisterName(
                                    declaration_map, function, reg, preview_seed_line),
                                true,
                                false};
                    }
                }

                std::string preview_error;
                if (preview_seed_line <= current - 2 &&
                    context.evaluate_inline_range(preview_seed_line,
                                                  current - 2,
                                                  &preview_values,
                                                  &preview_error)) {
                    const StraightLineValue callee = GetRegisterValue(preview_values,
                                                                      declaration_map,
                                                                      function,
                                                                      previous_instruction.a,
                                                                      call_render_line);
                    const std::vector<StraightLineValue> arguments =
                        context.call_arguments_for(preview_values,
                                                   previous_instruction.a,
                                                   previous_instruction.b,
                                                   call_render_line);
                    if (callee.valid) {
                        iterator_expression = RenderCallExpression(callee,
                                                                   arguments,
                                                                   declaration_map,
                                                                   function,
                                                                   call_render_line,
                                                                   register_count);
                        recovered_iterator_expression = true;
                    }
                }
            }

            if (!recovered_iterator_expression && !lines.empty()) {
                std::vector<std::string> lhs_names;
                std::string rhs_expression;
                const bool parsed_assignment = ParseRenderedAssignmentLine(
                    lines.back(),
                    nullptr,
                    &lhs_names,
                    &rhs_expression);
                if (parsed_assignment && lhs_names.size() == 3) {
                    bool names_match = true;
                    for (int index = 0; index < 3; ++index) {
                        if (lhs_names[static_cast<std::size_t>(index)] !=
                            GetStraightLineRegisterName(declaration_map,
                                                        function,
                                                        previous_instruction.a + index,
                                                        call_render_line)) {
                            names_match = false;
                            break;
                        }
                    }
                    if (names_match) {
                        iterator_expression = rhs_expression;
                        recovered_iterator_expression = true;
                        lines.pop_back();
                    }
                }
            }

            if (!recovered_iterator_expression) {
                recovered_iterator_expression = try_recover_direct_iterator_tuple();
            }

            if (recovered_iterator_expression) {
                std::vector<StraightLineValue> post_loop_values = values;
                std::vector<bool> post_loop_declared = declared;
                for (int reg = previous_instruction.a;
                     reg <= previous_instruction.a + 2 && reg < register_count;
                     ++reg) {
                    post_loop_values[static_cast<std::size_t>(reg)] =
                        StraightLineValue{};
                    post_loop_declared[static_cast<std::size_t>(reg)] = false;
                }

                values = post_loop_values;
                declared = post_loop_declared;

                std::vector<std::string> loop_names;
                const int first_target = previous_instruction.a + 3;
                const int last_target = previous_instruction.a + 2 + tfor.c;
                for (int reg = first_target; reg <= last_target && reg < register_count;
                     ++reg) {
                    const std::string name = GetStraightLineRegisterName(
                        declaration_map, function, reg, body_start);
                    loop_names.push_back(name);
                    declared[static_cast<std::size_t>(reg)] = true;
                    values[static_cast<std::size_t>(reg)] =
                        StraightLineValue{name, true, false};
                }
                const std::string statement =
                    RenderGenericForStatementHeader(loop_names, iterator_expression);
                AppendIndentedStatement(&lines, "  ", statement);
                if (body_start <= jump_target - 1 &&
                    !context.render_range(body_start, jump_target - 1, body_start, loop_exit)) {
                    return ErrorResult(error, error == nullptr ? std::string() : *error);
                }
                values = post_loop_values;
                declared = post_loop_declared;
                AppendIndentedStatement(&lines, "  ", "end");
                context.invalidate_values(call_render_line);
                if (FinalizePendingLuaJump(&lines, &pending_jump_target, body_start, loop_exit)) {
                    return ReturnResult();
                }
                return ContinueResult(loop_exit);
            }
        }
    }
    if (jump_target > current + 1 &&
        decompiler::IsStructuredSiblingBranchJump(
            context.control_flow_graph, current, current + 1, jump_target)) {
        return ContinueResult(jump_target);
    }
    if (jump_target > current + 1 &&
        !decompiler::HasIncomingEdgeIntoRange(
            context.control_flow_graph, current + 1, jump_target - 1, current)) {
        return ContinueResult(jump_target);
    }
    if (jump_target > current + 1 && loop_exit_target != -1 &&
        jump_target < loop_exit_target) {
        const bool reaches_shared_loop_tail =
            (loop_continue_target != -1 &&
             decompiler::CanReachTargetLine(
                 context.control_flow_graph, jump_target, loop_continue_target)) ||
            decompiler::CanReachTargetLine(
                context.control_flow_graph, jump_target, loop_exit_target);
        if (reaches_shared_loop_tail) {
            if (context.trace_render) {
                std::ostringstream stream;
                stream << "shared-loop-tail current=" << current
                       << " target=" << jump_target
                       << " continue=" << loop_continue_target
                       << " exit=" << loop_exit_target;
                trace(stream.str());
            }
            return ContinueResult(jump_target);
        }
    }
    if (jump_target > current + 1 &&
        jump_target + 1 <= instruction_count) {
        const Lua51Instruction& trailing_condition =
            instructions[static_cast<std::size_t>(jump_target - 1)];
        const Lua51Instruction& trailing_guard_jump =
            instructions[static_cast<std::size_t>(jump_target)];
        if (IsConditionalOpcode(trailing_condition.opcode) &&
            trailing_guard_jump.opcode == Lua51Opcode::kJmp) {
            const int loop_back_target = compute_jump_target(jump_target + 1,
                                                             trailing_guard_jump.sbx);
            if (loop_back_target == current + 1) {
                const int condition_render_line = static_cast<int>(
                    trailing_condition.line == 0 ? jump_target
                                                 : trailing_condition.line);
                const std::string trailing_condition_text =
                    context.render_condition(trailing_condition, condition_render_line);
                if (!trailing_condition_text.empty()) {
                    AppendIndentedStatement(&lines, "  ", "repeat");
                    const std::vector<StraightLineValue> saved_values = values;
                    const std::vector<bool> saved_declared = declared;
                    if (current + 1 <= jump_target - 1 &&
                        !context.render_range(current + 1,
                                              jump_target - 1,
                                              current + 1,
                                              jump_target + 2)) {
                        return ErrorResult(error, error == nullptr ? std::string() : *error);
                    }
                    values = saved_values;
                    declared = saved_declared;
                    AppendIndentedStatement(
                        &lines, "  ", RenderRepeatFooter(trailing_condition_text));
                    context.invalidate_values(condition_render_line);
                    if (FinalizePendingLuaJump(
                            &lines, &pending_jump_target, current + 1, jump_target + 2)) {
                        return ReturnResult();
                    }
                    return ContinueResult(jump_target + 2);
                }
            }
        }
    }
    if (try_render_jump_return(current, jump_target, values)) {
        return ReturnResult();
    }
    if (resolved_jump_target != jump_target &&
        try_render_jump_return(current, resolved_jump_target, values)) {
        return ReturnResult();
    }
    if (loop_continue_target != -1 && resolved_jump_target == loop_continue_target) {
        return ReturnResult();
    }
    if (loop_exit_target != -1 && resolved_jump_target == loop_exit_target) {
        AppendIndentedStatement(&lines, "  ", "break");
        return ReturnResult();
    }
    const int ancestor_jump_target = FindAncestorLuaLoopTarget(
        loop_target_stack, jump_target, loop_continue_target, loop_exit_target);
    if (ancestor_jump_target != -1) {
        pending_jump_target = ancestor_jump_target;
        return ReturnResult();
    }
    const int resolved_ancestor_jump_target =
        resolved_jump_target != jump_target
            ? FindAncestorLuaLoopTarget(loop_target_stack,
                                        resolved_jump_target,
                                        loop_continue_target,
                                        loop_exit_target)
            : -1;
    if (resolved_ancestor_jump_target != -1) {
        pending_jump_target = resolved_ancestor_jump_target;
        return ReturnResult();
    }
    if (jump_target >= 1 && jump_target + 2 <= current &&
        static_cast<std::size_t>(jump_target + 1) <= instructions.size()) {
        const Lua51Instruction& loop_condition =
            instructions[static_cast<std::size_t>(jump_target - 1)];
        const Lua51Instruction& loop_guard_jump =
            instructions[static_cast<std::size_t>(jump_target)];
        if (IsConditionalOpcode(loop_condition.opcode) &&
            loop_guard_jump.opcode == Lua51Opcode::kJmp) {
            const int guard_target =
                compute_jump_target(jump_target + 1, loop_guard_jump.sbx);
            const int resolved_guard_target =
                decompiler::ResolveTerminalJumpTarget(instructions, guard_target);
            const bool ends_current_range =
                guard_target == end_line + 1 || resolved_guard_target == end_line + 1;
            const bool exits_inner_loop = guard_target == current + 1;
            const bool exits_parent =
                exits_inner_loop || guard_target == loop_continue_target ||
                resolved_guard_target == loop_continue_target ||
                (loop_exit_target != -1 && guard_target == loop_exit_target);
            const int loop_body_start = jump_target + 2;
            const std::size_t body_offset =
                context.instruction_output_offsets[static_cast<std::size_t>(loop_body_start)];
            if (context.trace_render) {
                std::ostringstream stream;
                stream << "retro-loop current=" << current
                       << " target=" << jump_target
                       << " guard_target=" << guard_target
                       << " continue=" << loop_continue_target
                       << " exit=" << loop_exit_target
                       << " body_start=" << loop_body_start
                       << " body_offset=" << body_offset
                       << " line_count=" << lines.size()
                       << " ends_range=" << (ends_current_range ? 1 : 0)
                       << " exits_parent=" << (exits_parent ? 1 : 0);
                trace(stream.str());
            }
            if ((exits_parent || ends_current_range) &&
                loop_body_start >= context.range_start_line && loop_body_start <= current - 1 &&
                body_offset >= context.range_base_offset && body_offset < lines.size()) {
                const int condition_render_line = static_cast<int>(
                    loop_condition.line == 0 ? jump_target : loop_condition.line);
                const std::string rendered_condition =
                    context.render_condition(loop_condition, condition_render_line);
                if (context.trace_render) {
                    std::ostringstream stream;
                    stream << "retro-loop condition='" << rendered_condition << "'";
                    trace(stream.str());
                }
                if (!rendered_condition.empty()) {
                    std::vector<std::string> body_lines(
                        lines.begin() + static_cast<std::ptrdiff_t>(body_offset),
                        lines.end());
                    lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(body_offset),
                               lines.end());
                    AppendIndentedStatement(
                        &lines, "  ", RenderWhileStatementHeader(rendered_condition));
                    for (const std::string& body_line : body_lines) {
                        lines.push_back("  " + body_line);
                    }
                    AppendIndentedStatement(&lines, "  ", "end");
                    context.invalidate_values(condition_render_line);
                    if (ends_current_range) {
                        return ContinueResult(guard_target);
                    }
                    if (exits_inner_loop) {
                        return ContinueResult(guard_target);
                    }
                    if (guard_target == loop_continue_target) {
                        return ReturnResult();
                    }
                    if (loop_exit_target != -1 &&
                        guard_target == loop_exit_target) {
                        AppendIndentedStatement(&lines, "  ", "break");
                        return ReturnResult();
                    }
                }
            }
        }
    }
    if (jump_target >= 1 && jump_target + 2 <= current &&
        static_cast<std::size_t>(jump_target + 1) <= instructions.size()) {
        for (int condition_line = jump_target; condition_line <= current - 1;
             ++condition_line) {
            if (static_cast<std::size_t>(condition_line + 1) > instructions.size()) {
                break;
            }
            const Lua51Instruction& loop_condition =
                instructions[static_cast<std::size_t>(condition_line - 1)];
            const Lua51Instruction& loop_guard_jump =
                instructions[static_cast<std::size_t>(condition_line)];
            if (!IsConditionalOpcode(loop_condition.opcode) ||
                loop_guard_jump.opcode != Lua51Opcode::kJmp) {
                continue;
            }

            const int guard_target =
                compute_jump_target(condition_line + 1, loop_guard_jump.sbx);
            const int resolved_guard_target =
                decompiler::ResolveTerminalJumpTarget(instructions, guard_target);
            const bool ends_current_range =
                guard_target == end_line + 1 || resolved_guard_target == end_line + 1;
            const bool exits_inner_loop = guard_target == current + 1;
            const bool exits_parent =
                exits_inner_loop || guard_target == loop_continue_target ||
                resolved_guard_target == loop_continue_target ||
                (loop_exit_target != -1 && guard_target == loop_exit_target);
            const int loop_body_start = condition_line + 2;
            if ((!exits_parent && !ends_current_range) || loop_body_start > current - 1) {
                continue;
            }

            std::vector<StraightLineValue> preview_values(
                static_cast<std::size_t>(register_count));
            std::string preview_error;
            if (!context.evaluate_inline_range(jump_target,
                                               condition_line - 1,
                                               &preview_values,
                                               &preview_error)) {
                continue;
            }

            const int condition_render_line = static_cast<int>(
                loop_condition.line == 0 ? condition_line : loop_condition.line);
            const std::string rendered_condition =
                RenderLuaConditionWithValues(preview_values,
                                             declaration_map,
                                             function,
                                             loop_condition,
                                             condition_render_line);
            if (rendered_condition.empty()) {
                continue;
            }

            const std::size_t body_offset =
                context.instruction_output_offsets[static_cast<std::size_t>(loop_body_start)];
            if (loop_body_start < context.range_start_line ||
                body_offset < context.range_base_offset || body_offset >= lines.size()) {
                continue;
            }

            if (context.trace_render) {
                std::ostringstream stream;
                stream << "retro-loop-preamble current=" << current
                       << " target=" << jump_target
                       << " cond=" << condition_line
                       << " guard_target=" << guard_target
                       << " continue=" << loop_continue_target
                       << " exit=" << loop_exit_target
                       << " body_start=" << loop_body_start
                       << " body_offset=" << body_offset
                       << " ends_range=" << (ends_current_range ? 1 : 0);
                trace(stream.str());
            }

            std::vector<std::string> body_lines(
                lines.begin() + static_cast<std::ptrdiff_t>(body_offset), lines.end());
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(body_offset),
                       lines.end());
            AppendIndentedStatement(
                &lines, "  ", RenderWhileStatementHeader(rendered_condition));
            for (const std::string& body_line : body_lines) {
                lines.push_back("  " + body_line);
            }
            AppendIndentedStatement(&lines, "  ", "end");
            context.invalidate_values(condition_render_line);
            if (ends_current_range) {
                return ContinueResult(guard_target);
            }
            if (exits_inner_loop) {
                return ContinueResult(guard_target);
            }
            if (guard_target == loop_continue_target) {
                return ReturnResult();
            }
            if (loop_exit_target != -1 && guard_target == loop_exit_target) {
                AppendIndentedStatement(&lines, "  ", "break");
                return ReturnResult();
            }
        }
    }

    return ErrorResult(error,
                       "unsupported_unstructured_jump@" + std::to_string(current) +
                           "->" + std::to_string(jump_target) +
                           "(continue=" + std::to_string(loop_continue_target) +
                           ",exit=" + std::to_string(loop_exit_target) + ")");
}

}  // namespace refii::lua
