#include "tools/decompiler/loop_condition_render.h"

namespace refii::lua {

bool TryRenderLuaLoopConditionText(const LuaLoopConditionRenderContext& context,
                                   int preview_start,
                                   int preview_end,
                                   const Lua51Instruction& condition_instruction,
                                   int condition_line,
                                   std::vector<StraightLineValue>* values,
                                   std::string* text,
                                   std::string* error) {
    if (values == nullptr || text == nullptr) {
        if (error != nullptr) {
            *error = "loop_condition_output_missing";
        }
        return false;
    }

    std::vector<StraightLineValue> rendered_values = *values;
    if (preview_start <= preview_end) {
        if (!context.evaluate_inline_range) {
            if (error != nullptr) {
                *error = "loop_condition_preview_missing";
            }
            return false;
        }

        if (!context.evaluate_inline_range(
                preview_start, preview_end, &rendered_values, error)) {
            return false;
        }
    }

    *text = RenderLuaConditionWithValues(rendered_values,
                                         context.declarations,
                                         context.function,
                                         condition_instruction,
                                         condition_line);
    if (text->empty()) {
        if (error != nullptr) {
            *error = "loop_condition_render_failed@" + std::to_string(condition_line);
        }
        return false;
    }

    *values = std::move(rendered_values);
    return true;
}

bool TryRenderLuaChainedLoopConditions(
    const LuaLoopConditionRenderContext& context,
    const decompiler::LuaChainedConditionLoopBlock& block,
    std::vector<StraightLineValue>* values,
    std::vector<std::string>* conditions,
    std::string* error) {
    if (values == nullptr || conditions == nullptr) {
        if (error != nullptr) {
            *error = "loop_condition_sequence_output_missing";
        }
        return false;
    }

    std::vector<StraightLineValue> preview_values = *values;
    conditions->clear();
    for (std::size_t i = 0; i < block.condition_lines.size(); ++i) {
        const int segment_start = block.segment_starts[i];
        const int condition_line = block.condition_lines[i];
        const Lua51Instruction& loop_condition =
            context.instructions[static_cast<std::size_t>(condition_line - 1)];
        std::string loop_condition_text;
        if (!TryRenderLuaLoopConditionText(context,
                                           segment_start,
                                           condition_line - 1,
                                           loop_condition,
                                           static_cast<int>(loop_condition.line == 0
                                                                ? condition_line
                                                                : loop_condition.line),
                                           &preview_values,
                                           &loop_condition_text,
                                           error)) {
            return false;
        }
        conditions->push_back(std::move(loop_condition_text));
    }

    *values = std::move(preview_values);
    return true;
}

std::string BuildLuaWhileHeader(const std::vector<std::string>& conditions) {
    if (conditions.empty()) {
        return RenderWhileStatementHeader("true");
    }

    std::string expression;
    for (std::size_t i = 0; i < conditions.size(); ++i) {
        if (expression.empty()) {
            expression = conditions[i];
        } else {
            expression = RenderLogicalExpression(expression, "and", conditions[i]);
        }
    }
    return RenderWhileStatementHeader(expression);
}

}  // namespace refii::lua
