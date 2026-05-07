#include "tools/decompiler/loop_render.h"

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/loop_block_render.h"
#include "tools/decompiler/loop_condition_render.h"
#include "tools/decompiler/loop_statement_render.h"
#include "tools/decompiler/render_state.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/testset_render.h"

#include <sstream>

namespace refii::lua {
namespace {

bool IsConditionalOpcode(Lua51Opcode opcode) {
    return decompiler::IsConditionalBranchOpcode(opcode);
}

LuaLoopRenderResult ErrorResult(std::string* error, std::string message) {
    if (error != nullptr) {
        *error = std::move(message);
    }
    return LuaLoopRenderResult{.disposition = LuaLoopRenderDisposition::kError};
}

LuaLoopRenderResult ContinueResult(int next_current) {
    return LuaLoopRenderResult{
        .disposition = LuaLoopRenderDisposition::kContinue,
        .next_current = next_current,
    };
}

LuaLoopRenderResult ReturnResult() {
    return LuaLoopRenderResult{
        .disposition = LuaLoopRenderDisposition::kReturn,
        .next_current = -1,
    };
}

bool TryFindLuaPlainBackJumpLoop(const std::vector<Lua51Instruction>& instructions,
                                 int current,
                                 int end_line,
                                 int* loop_back_line,
                                 int* after_line) {
    if (loop_back_line == nullptr || after_line == nullptr ||
        current < 1 || static_cast<std::size_t>(current) > instructions.size()) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    const auto is_guard_jump_payload = [&](int candidate_line) {
        return candidate_line > 1 &&
               static_cast<std::size_t>(candidate_line - 1) <= instructions.size() &&
               IsConditionalOpcode(
                   instructions[static_cast<std::size_t>(candidate_line - 2)].opcode);
    };
    const auto matches_loop_back = [&](int candidate_line) {
        if (candidate_line <= current ||
            candidate_line < 1 ||
            candidate_line > instruction_count) {
            return false;
        }
        const Lua51Instruction& maybe_loop_back =
            instructions[static_cast<std::size_t>(candidate_line - 1)];
        if (maybe_loop_back.opcode != Lua51Opcode::kJmp) {
            return false;
        }
        if (is_guard_jump_payload(candidate_line)) {
            return false;
        }
        return decompiler::ComputeJumpTargetLine(candidate_line,
                                                 maybe_loop_back.sbx,
                                                 instruction_count) == current;
    };

    for (int candidate = std::min(end_line, instruction_count); candidate >= current + 1;
         --candidate) {
        if (!matches_loop_back(candidate)) {
            continue;
        }
        *loop_back_line = candidate;
        *after_line = candidate + 1;
        return true;
    }

    const int external_loop_back_line = end_line + 1;
    if (matches_loop_back(external_loop_back_line)) {
        *loop_back_line = external_loop_back_line;
        *after_line = external_loop_back_line + 1;
        return true;
    }

    return false;
}

bool TryRenderLuaSingleStatementBackJumpLoop(const LuaLoopRenderContext& context,
                                             const LuaLoopBlockRenderContext& block_render_context,
                                             int current,
                                             int end_line,
                                             int line,
                                             int loop_continue_target,
                                             int loop_exit_target,
                                             std::string* error,
                                             LuaLoopRenderResult* result) {
    if (result == nullptr || context.values == nullptr || context.declared == nullptr) {
        return false;
    }
    if (current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
    }
    if (!context.call_arguments_for || !context.return_values_for || !context.setlist_values_for ||
        !context.render_child || !context.resolve_setlist_block) {
        return false;
    }

    std::vector<std::string> preview_lines;
    std::vector<StraightLineValue> preview_values = *context.values;
    std::vector<bool> preview_declared = *context.declared;
    const LuaStatementRenderContext statement_context{
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .lines = &preview_lines,
        .values = &preview_values,
        .declared = &preview_declared,
        .call_arguments_for = context.call_arguments_for,
        .return_values_for = context.return_values_for,
        .setlist_values_for = context.setlist_values_for,
        .render_child = context.render_child,
        .resolve_setlist_block = context.resolve_setlist_block,
        .trace_render = context.trace_render,
        .trace = context.trace,
    };
    std::string statement_error;
    const LuaStatementRenderResult statement_result =
        TryRenderLuaStraightLineStatement(
            statement_context, current, end_line, current, line, &statement_error);
    if (statement_result.disposition != LuaStatementRenderDisposition::kContinue) {
        return false;
    }

    const int loop_back_line = statement_result.next_current;
    if (loop_back_line <= current ||
        static_cast<std::size_t>(loop_back_line) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& maybe_loop_back =
        context.instructions[static_cast<std::size_t>(loop_back_line - 1)];
    if (maybe_loop_back.opcode != Lua51Opcode::kJmp ||
        decompiler::ComputeJumpTargetLine(loop_back_line,
                                          maybe_loop_back.sbx,
                                          static_cast<int>(context.instructions.size())) != current) {
        return false;
    }

    if (preview_lines.empty()) {
        return false;
    }

    AppendIndentedStatement(context.lines, "  ", RenderWhileStatementHeader("true"));
    for (const std::string& rendered_line : preview_lines) {
        context.lines->push_back(rendered_line);
    }
    *context.values = std::move(preview_values);
    *context.declared = std::move(preview_declared);
    *result = FinalizeLuaEndClosedLoop(block_render_context,
                                       line,
                                       current,
                                       loop_back_line + 1,
                                       loop_back_line + 1,
                                       loop_back_line + 1,
                                       loop_continue_target,
                                       loop_exit_target,
                                       LuaLoopRenderDisposition::kContinue,
                                       false);
    return true;
}

bool LuaTestSetFallbackNeedsMaterializedTarget(const std::vector<Lua51Instruction>& instructions,
                                               int start_line,
                                               int end_line) {
    if (start_line > end_line) {
        return false;
    }
    for (int line = start_line;
         line <= end_line && static_cast<std::size_t>(line) <= instructions.size();
         ++line) {
        const Lua51Instruction& instruction =
            instructions[static_cast<std::size_t>(line - 1)];
        if (instruction.opcode == Lua51Opcode::kJmp ||
            IsConditionalOpcode(instruction.opcode)) {
            return true;
        }
    }
    return false;
}

std::string GetLuaLoopTestSetMaterializationName(const RegisterDeclarations& declarations,
                                                 const Lua51Function& function,
                                                 int reg,
                                                 int line) {
    const std::string assignment_name =
        GetStraightLineAssignmentRegisterName(declarations, function, reg, line);
    if (!assignment_name.empty() && !IsRenderTemporaryName(assignment_name)) {
        return assignment_name;
    }

    const std::string register_name =
        GetStraightLineRegisterName(declarations, function, reg, line);
    if (!register_name.empty() && !IsRenderTemporaryName(register_name)) {
        return register_name;
    }

    return {};
}

int FindLuaLoopTestSetMaterializationLine(const std::vector<Lua51Instruction>& instructions,
                                          const RegisterDeclarations& declarations,
                                          const Lua51Function& function,
                                          int register_count,
                                          int reg,
                                          int assignment_line) {
    if (reg < 0) {
        return assignment_line;
    }

    const auto has_meaningful_name_at = [&](int line) {
        return !GetLuaLoopTestSetMaterializationName(declarations, function, reg, line).empty();
    };

    if (has_meaningful_name_at(assignment_line)) {
        return assignment_line;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    for (int line = assignment_line + 1; line <= instruction_count; ++line) {
        const std::vector<bool> written =
            CollectWrittenLuaRegistersInRange(instructions, register_count, line, line);
        if (static_cast<std::size_t>(reg) < written.size() &&
            written[static_cast<std::size_t>(reg)]) {
            break;
        }
        if (has_meaningful_name_at(line)) {
            return line;
        }
    }

    return assignment_line;
}

void AssignLuaLoopTestSetMaterializedTarget(std::vector<std::string>* lines,
                                            std::vector<StraightLineValue>* values,
                                            std::vector<bool>* declared,
                                            const RegisterDeclarations& declarations,
                                            const Lua51Function& function,
                                            int reg,
                                            int name_line,
                                            const StraightLineValue& value) {
    if (lines == nullptr || values == nullptr || declared == nullptr ||
        reg < 0 || static_cast<std::size_t>(reg) >= values->size()) {
        return;
    }

    const std::string name =
        GetLuaLoopTestSetMaterializationName(declarations, function, reg, name_line);
    if (name.empty()) {
        AssignRegister(lines,
                       values,
                       declared,
                       declarations,
                       function,
                       reg,
                       name_line,
                       value.text,
                       value.multiple_results);
        return;
    }

    const std::string prior_name =
        GetStraightLineRegisterName(declarations, function, reg, std::max(1, name_line - 1));
    const bool emit_local =
        (prior_name != name || !(*declared)[static_cast<std::size_t>(reg)]) &&
        reg >= function.parameter_count;
    AppendIndentedStatement(lines, "  ", RenderAssignmentStatement(name, value.text, emit_local));
    if (emit_local) {
        (*declared)[static_cast<std::size_t>(reg)] = true;
    }
    (*values)[static_cast<std::size_t>(reg)] =
        StraightLineValue{name, true, value.multiple_results};
}

}  // namespace

LuaLoopRenderResult TryRenderLuaRangeEntryLoop(const LuaLoopRenderContext& context,
                                               int start_line,
                                               int end_line,
                                               int loop_continue_target,
                                               int loop_exit_target,
                                               std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.render_condition || !context.render_range || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return ErrorResult(error, "range_entry_context_missing");
    }

    auto& lines = *context.lines;
    auto& values = *context.values;
    auto& declared = *context.declared;
    const auto& instructions = context.instructions;
    const LuaLoopBlockRenderContext block_render_context{
        .lines = &lines,
        .values = &values,
        .declared = &declared,
        .render_range = context.render_range,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    const LuaLoopConditionRenderContext condition_context{
        .instructions = instructions,
        .declarations = context.declarations,
        .function = context.function,
        .evaluate_inline_range = context.evaluate_inline_range,
    };
    decompiler::LuaRangeEntryLoopBlock range_entry_loop_block;
    if (!decompiler::TryBuildLuaRangeEntryLoopBlock(instructions,
                                                    start_line,
                                                    end_line,
                                                    loop_continue_target,
                                                    loop_exit_target,
                                                    &range_entry_loop_block)) {
        return {};
    }

    const auto trace = [&](std::string_view message) {
        if (context.trace_render && context.trace) {
            context.trace(message);
        }
    };

    const Lua51Instruction& start_condition =
        instructions[static_cast<std::size_t>(range_entry_loop_block.condition_line - 1)];
    if (context.trace_render) {
        std::ostringstream stream;
        stream << "start-range candidate start=" << start_line
               << " cond=" << range_entry_loop_block.condition_line
               << " guard_target=" << range_entry_loop_block.guard_target
               << " resolved_guard_target=" << range_entry_loop_block.resolved_guard_target
               << " continue=" << loop_continue_target
               << " exit=" << loop_exit_target
               << " ends_range=" << (range_entry_loop_block.ends_current_range ? 1 : 0)
               << " exits_parent=" << (range_entry_loop_block.exits_parent ? 1 : 0);
        trace(stream.str());
    }
    std::string loop_condition;
    std::string preview_error;
    if (!TryRenderLuaLoopConditionText(condition_context,
                                       range_entry_loop_block.preamble_start,
                                       range_entry_loop_block.preamble_start == -1
                                           ? -1
                                           : range_entry_loop_block.condition_line - 1,
                                       start_condition,
                                       range_entry_loop_block.condition_render_line,
                                       &values,
                                       &loop_condition,
                                       &preview_error)) {
        if (range_entry_loop_block.preamble_start == -1) {
            loop_condition = context.render_condition(start_condition,
                                                     range_entry_loop_block.condition_render_line);
        } else {
            return {};
        }
    }
    if (loop_condition.empty()) {
        return {};
    }

    AppendIndentedStatement(&lines, "  ", RenderWhileStatementHeader(loop_condition));
    if (!RenderLuaLoopBodyRange(block_render_context,
                                range_entry_loop_block.body_start,
                                range_entry_loop_block.body_end,
                                range_entry_loop_block.continue_target,
                                range_entry_loop_block.exit_target,
                                error)) {
        return ErrorResult(error, error == nullptr ? std::string() : *error);
    }
    if (range_entry_loop_block.ends_current_range) {
        AppendIndentedStatement(&lines, "  ", "end");
        context.invalidate_values(range_entry_loop_block.condition_render_line);
        return ReturnResult();
    }
    return FinalizeLuaEndClosedLoop(block_render_context,
                                    range_entry_loop_block.condition_render_line,
                                    range_entry_loop_block.continue_target,
                                    range_entry_loop_block.guard_target,
                                    range_entry_loop_block.guard_target,
                                    range_entry_loop_block.resolved_guard_target,
                                    loop_continue_target,
                                    loop_exit_target,
                                    LuaLoopRenderDisposition::kNotHandled);
}

LuaLoopRenderResult TryRenderLuaLoopControlFlow(const LuaLoopRenderContext& context,
                                                int current,
                                                int end_line,
                                                int loop_continue_target,
                                                int loop_exit_target,
                                                int line,
                                                std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.call_arguments_for || !context.evaluate_inline_range ||
        !context.render_condition || !context.render_range ||
        !context.invalidate_values || !context.finalize_pending_jump) {
        return ErrorResult(error, "loop_context_missing");
    }

    auto& lines = *context.lines;
    auto& values = *context.values;
    auto& declared = *context.declared;
    const auto& instructions = context.instructions;
    const auto& declaration_map = context.declarations;
    const auto& function = context.function;
    const int register_count = context.register_count;
    const LuaLoopBlockRenderContext block_render_context{
        .lines = &lines,
        .values = &values,
        .declared = &declared,
        .render_range = context.render_range,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    const LuaLoopConditionRenderContext condition_context{
        .instructions = instructions,
        .declarations = declaration_map,
        .function = function,
        .evaluate_inline_range = context.evaluate_inline_range,
    };

    if (current < 1 || static_cast<std::size_t>(current) > instructions.size()) {
        return {};
    }

    const Lua51Instruction& instruction = instructions[static_cast<std::size_t>(current - 1)];

    decompiler::LuaChainedConditionLoopBlock chained_condition_loop_block;
    std::vector<StraightLineValue> validation_preview_values = values;
    int expected_segment_start = current;
    const bool nested_same_head_loop =
        loop_continue_target != -1 && loop_exit_target != -1 && current == loop_continue_target;
    if (!nested_same_head_loop &&
        decompiler::TryBuildLuaChainedConditionLoopBlock(
            instructions,
            current,
            end_line,
            loop_continue_target,
            loop_exit_target,
            [&](int segment_start, int condition_line) {
                if (segment_start != expected_segment_start) {
                    return false;
                }

                std::vector<StraightLineValue> segment_values = validation_preview_values;
                std::string preview_error;
                if (!context.evaluate_inline_range(segment_start,
                                                   condition_line - 1,
                                                   &segment_values,
                                                   &preview_error)) {
                    return false;
                }

                const Lua51Instruction& loop_condition =
                    instructions[static_cast<std::size_t>(condition_line - 1)];
                std::string loop_condition_text;
                if (!TryRenderLuaLoopConditionText(
                        condition_context,
                        segment_start,
                        condition_line - 1,
                        loop_condition,
                        static_cast<int>(loop_condition.line == 0
                                             ? condition_line
                                             : loop_condition.line),
                        &validation_preview_values,
                        &loop_condition_text,
                        &preview_error)) {
                    return false;
                }
                if (loop_condition_text.empty()) {
                    return false;
                }

                expected_segment_start = condition_line + 2;
                return true;
            },
            &chained_condition_loop_block)) {
        std::vector<StraightLineValue> preview_values = values;
        std::vector<std::string> loop_conditions;
        std::string condition_error;
        if (!TryRenderLuaChainedLoopConditions(condition_context,
                                               chained_condition_loop_block,
                                               &preview_values,
                                               &loop_conditions,
                                               &condition_error)) {
            return ErrorResult(error, std::move(condition_error));
        }

        const std::string loop_header = BuildLuaWhileHeader(loop_conditions);

        AppendIndentedStatement(&lines, "  ", loop_header);
        values = preview_values;
        if (!RenderLuaLoopBodyRange(block_render_context,
                                    chained_condition_loop_block.body_start,
                                    chained_condition_loop_block.body_end,
                                    current,
                                    chained_condition_loop_block.loop_exit,
                                    error)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        return FinalizeLuaEndClosedLoop(block_render_context,
                                        line,
                                        current,
                                        chained_condition_loop_block.loop_exit,
                                        chained_condition_loop_block.loop_exit,
                                        chained_condition_loop_block.loop_exit,
                                        loop_continue_target,
                                        loop_exit_target,
                                        LuaLoopRenderDisposition::kContinue);
    }

    decompiler::LuaGuardedContinueBlock guarded_continue_block;
    if (decompiler::TryBuildLuaGuardedContinueBlock(instructions,
                                                    current,
                                                    end_line,
                                                    loop_continue_target,
                                                    &guarded_continue_block)) {
        const Lua51Instruction& guard_condition =
            instructions[static_cast<std::size_t>(guarded_continue_block.condition_line - 1)];
        std::vector<StraightLineValue> preview_values = values;
        std::string preview_error;
        std::string guard_condition_text;
        if (TryRenderLuaLoopConditionText(condition_context,
                                          current,
                                          guarded_continue_block.condition_line - 1,
                                          guard_condition,
                                          guarded_continue_block.condition_render_line,
                                          &preview_values,
                                          &guard_condition_text,
                                          &preview_error)) {
            AppendIndentedStatement(&lines, "  ", RenderIfStatementHeader(guard_condition_text));
            values = preview_values;
            if (!RenderLuaLoopBodyRange(block_render_context,
                                        guarded_continue_block.body_start,
                                        guarded_continue_block.body_end,
                                        loop_continue_target,
                                        loop_exit_target,
                                        error)) {
                return ErrorResult(error, error == nullptr ? std::string() : *error);
            }
            return FinalizeLuaEndClosedLoop(block_render_context,
                                            -1,
                                            loop_continue_target,
                                            loop_exit_target,
                                            loop_continue_target,
                                            loop_continue_target,
                                            loop_continue_target,
                                            loop_exit_target,
                                            LuaLoopRenderDisposition::kReturn,
                                            false);
        }
    }

    decompiler::LuaGenericForLoopBlock generic_for_block;
    if (decompiler::TryBuildLuaGenericForLoopBlock(instructions, current, &generic_for_block)) {
        const StraightLineValue callee = GetRegisterValue(values,
                                                          declaration_map,
                                                          function,
                                                          generic_for_block.base_register,
                                                          line);
        const std::vector<StraightLineValue> arguments =
            context.call_arguments_for(values,
                                       generic_for_block.base_register,
                                       instruction.b,
                                       line);
        const std::string call_expression = RenderCallExpression(callee,
                                                                 arguments,
                                                                 declaration_map,
                                                                 function,
                                                                 line,
                                                                 register_count);
        LuaLoopBindingState binding;
        std::string statement;
        if (!PrepareLuaGenericForStatement(declaration_map,
                                           function,
                                           register_count,
                                           generic_for_block,
                                           call_expression,
                                           &values,
                                           &declared,
                                           &binding,
                                           &statement,
                                           error)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        AppendIndentedStatement(&lines, "  ", statement);
        if (!RenderLuaLoopBodyRange(block_render_context,
                                    generic_for_block.body_start,
                                    generic_for_block.body_end,
                                    generic_for_block.body_start,
                                    generic_for_block.loop_exit,
                                    error)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        RestoreLuaLoopBindingState(&values, &declared, &binding);
        return FinalizeLuaEndClosedLoop(block_render_context,
                                        line,
                                        generic_for_block.body_start,
                                        generic_for_block.loop_exit,
                                        generic_for_block.loop_exit,
                                        generic_for_block.loop_exit,
                                        loop_continue_target,
                                        loop_exit_target,
                                        LuaLoopRenderDisposition::kContinue,
                                        false);
    }

    decompiler::LuaNumericForLoopBlock numeric_for_block;
    if (decompiler::TryBuildLuaNumericForLoopBlock(instructions, current, &numeric_for_block)) {
        const StraightLineValue initial = GetRegisterValue(values,
                                                           declaration_map,
                                                           function,
                                                           numeric_for_block.base_register,
                                                           line);
        const StraightLineValue limit = GetRegisterValue(values,
                                                         declaration_map,
                                                         function,
                                                         numeric_for_block.base_register + 1,
                                                         line);
        const StraightLineValue step = GetRegisterValue(values,
                                                        declaration_map,
                                                        function,
                                                        numeric_for_block.base_register + 2,
                                                        line);
        LuaLoopBindingState binding;
        std::string statement;
        if (!PrepareLuaNumericForStatement(declaration_map,
                                           function,
                                           register_count,
                                           numeric_for_block,
                                           initial.text,
                                           limit.text,
                                           step.text,
                                           &values,
                                           &declared,
                                           &binding,
                                           &statement,
                                           error)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        AppendIndentedStatement(&lines, "  ", statement);
        if (!RenderLuaLoopBodyRange(block_render_context,
                                    numeric_for_block.body_start,
                                    numeric_for_block.body_end,
                                    current,
                                    numeric_for_block.loop_exit,
                                    error)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        RestoreLuaLoopBindingState(&values, &declared, &binding);
        return FinalizeLuaEndClosedLoop(block_render_context,
                                        line,
                                        current,
                                        numeric_for_block.loop_exit,
                                        numeric_for_block.loop_exit,
                                        numeric_for_block.loop_exit,
                                        loop_continue_target,
                                        loop_exit_target,
                                        LuaLoopRenderDisposition::kContinue,
                                        false);
    }

    decompiler::LuaRepeatLoopBlock repeat_loop_block;
    if (decompiler::TryBuildLuaRepeatLoopBlock(instructions,
                                               current,
                                               end_line,
                                               &repeat_loop_block)) {
        const Lua51Instruction& repeat_condition =
            instructions[static_cast<std::size_t>(repeat_loop_block.condition_line - 1)];
        const std::string repeat_condition_text =
            context.render_condition(repeat_condition,
                                     static_cast<int>(repeat_condition.line == 0
                                                          ? repeat_loop_block.condition_line
                                                          : repeat_condition.line));
        if (repeat_condition_text.empty()) {
            return {};
        }
        AppendIndentedStatement(&lines, "  ", "repeat");
        if (!context.render_range(repeat_loop_block.body_start,
                                  repeat_loop_block.body_end,
                                  current,
                                  repeat_loop_block.after_line)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        AppendIndentedStatement(&lines, "  ", BuildLuaRepeatFooter(repeat_condition_text));
        context.invalidate_values(line);
        if (context.finalize_pending_jump(current, repeat_loop_block.after_line)) {
            return ReturnResult();
        }
        return ContinueResult(repeat_loop_block.after_line);
    }

    int plain_loop_back_line = -1;
    int plain_loop_after_line = -1;
    if ((loop_continue_target == -1 || current != loop_continue_target) &&
        TryFindLuaPlainBackJumpLoop(
            instructions, current, end_line, &plain_loop_back_line, &plain_loop_after_line)) {
        AppendIndentedStatement(&lines, "  ", RenderWhileStatementHeader("true"));
        if (!RenderLuaLoopBodyRange(block_render_context,
                                    current,
                                    plain_loop_back_line - 1,
                                    current,
                                    plain_loop_after_line,
                                    error)) {
            return ErrorResult(error, error == nullptr ? std::string() : *error);
        }
        return FinalizeLuaEndClosedLoop(block_render_context,
                                        line,
                                        current,
                                        plain_loop_after_line,
                                        plain_loop_after_line,
                                        plain_loop_after_line,
                                        loop_continue_target,
                                        loop_exit_target,
                                        LuaLoopRenderDisposition::kContinue,
                                        false);
    }

    LuaLoopRenderResult single_statement_loop_result;
    if ((loop_continue_target == -1 || current != loop_continue_target) &&
        TryRenderLuaSingleStatementBackJumpLoop(context,
                                                block_render_context,
                                                current,
                                                end_line,
                                                line,
                                                loop_continue_target,
                                                loop_exit_target,
                                                error,
                                                &single_statement_loop_result)) {
        return single_statement_loop_result;
    }

    decompiler::LuaTestSetLoopControlBlock testset_loop_control_block;
    if (decompiler::TryBuildLuaTestSetLoopControlBlock(instructions,
                                                       current,
                                                       &testset_loop_control_block)) {
        if (LuaTestSetFallbackNeedsMaterializedTarget(instructions,
                                                      testset_loop_control_block.fallback_start,
                                                      testset_loop_control_block.fallback_end)) {
            return {};
        }
        const LuaTestSetFallbackContext testset_context{
            .preview_fallback =
                {
                    .declarations = declaration_map,
                    .function = function,
                    .evaluate_inline_range = context.evaluate_inline_range,
                },
        };
        const StraightLineValue initial_value =
            GetLuaTestSetInitialValue(values, testset_context, testset_loop_control_block, line);

        StraightLineValue merged_value;
        if (TryMergeLuaTestSetFallbackValues(testset_context,
                                             testset_loop_control_block,
                                             line,
                                             values,
                                             &merged_value)) {
            const int assignment_line =
                std::max(testset_loop_control_block.fallback_start,
                         testset_loop_control_block.fallback_end);
            const int materialization_line =
                FindLuaLoopTestSetMaterializationLine(instructions,
                                                     declaration_map,
                                                     function,
                                                     register_count,
                                                     testset_loop_control_block.target_register,
                                                     assignment_line);
            const bool materialize_local =
                !GetLuaLoopTestSetMaterializationName(declaration_map,
                                                     function,
                                                     testset_loop_control_block.target_register,
                                                     materialization_line)
                     .empty();
            if (materialize_local) {
                AssignLuaLoopTestSetMaterializedTarget(
                    &lines,
                    &values,
                    &declared,
                    declaration_map,
                    function,
                    testset_loop_control_block.target_register,
                    materialization_line,
                    merged_value);
            } else if (testset_loop_control_block.target_register >= 0 &&
                       static_cast<std::size_t>(testset_loop_control_block.target_register) <
                           values.size()) {
                values[static_cast<std::size_t>(testset_loop_control_block.target_register)] =
                    std::move(merged_value);
            }
            return ContinueResult(testset_loop_control_block.branch_target);
        }

        AssignLuaTestSetTarget(&lines,
                               &values,
                               &declared,
                               testset_context,
                               testset_loop_control_block,
                               line,
                               initial_value,
                               LuaTestSetFallbackNeedsMaterializedTarget(
                                   instructions,
                                   testset_loop_control_block.fallback_start,
                                   testset_loop_control_block.fallback_end));

        const std::string fallback_condition =
            BuildLuaTestSetFallbackCondition(declaration_map,
                                             function,
                                             testset_loop_control_block.target_register,
                                             line,
                                             testset_loop_control_block.invert_condition);

        if (testset_loop_control_block.fallback_start <=
            testset_loop_control_block.fallback_end) {
            AppendIndentedStatement(&lines, "  ", RenderIfStatementHeader(fallback_condition));
            if (!context.render_range(testset_loop_control_block.fallback_start,
                                      testset_loop_control_block.fallback_end,
                                      loop_continue_target,
                                      loop_exit_target)) {
                return ErrorResult(error, error == nullptr ? std::string() : *error);
            }
            AppendIndentedStatement(&lines, "  ", "end");
        }

        return ContinueResult(testset_loop_control_block.branch_target);
    }

    return {};
}

}  // namespace refii::lua
