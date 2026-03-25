#include "tools/decompiler/loop_render.h"

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/loop_block_render.h"
#include "tools/decompiler/loop_condition_render.h"
#include "tools/decompiler/loop_statement_render.h"
#include "tools/decompiler/render_state.h"
#include "tools/decompiler/rendered_lua_text.h"
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
    if (decompiler::TryBuildLuaChainedConditionLoopBlock(
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
        const std::string call_expression = RenderCallExpression(callee, arguments);
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

    decompiler::LuaTestSetLoopControlBlock testset_loop_control_block;
    if (decompiler::TryBuildLuaTestSetLoopControlBlock(instructions,
                                                       current,
                                                       &testset_loop_control_block)) {
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
            if (testset_loop_control_block.target_register >= 0 &&
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
                               initial_value);

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
