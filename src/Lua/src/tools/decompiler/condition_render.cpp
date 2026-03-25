#include "tools/decompiler/condition_render.h"

#include "tools/decompiler/rendered_lua_text.h"

namespace refii::lua {

std::string RenderLuaConditionWithValues(const std::vector<StraightLineValue>& state_values,
                                         const RegisterDeclarations& declarations,
                                         const Lua51Function& function,
                                         const Lua51Instruction& instruction,
                                         int line) {
    std::string expression;
    switch (instruction.opcode) {
        case Lua51Opcode::kEq: {
            const StraightLineValue lhs =
                GetRKValue(state_values, declarations, function, instruction.b, line);
            const StraightLineValue rhs =
                GetRKValue(state_values, declarations, function, instruction.c, line);
            expression =
                RenderBinaryExpression(lhs.text, instruction.a != 0 ? "~=" : "==", rhs.text);
            break;
        }
        case Lua51Opcode::kLt: {
            const StraightLineValue lhs =
                GetRKValue(state_values, declarations, function, instruction.b, line);
            const StraightLineValue rhs =
                GetRKValue(state_values, declarations, function, instruction.c, line);
            expression = RenderBinaryExpression(lhs.text, "<", rhs.text);
            if (instruction.a != 0) {
                expression = RenderUnaryExpression("not", expression);
            }
            break;
        }
        case Lua51Opcode::kLe: {
            const StraightLineValue lhs =
                GetRKValue(state_values, declarations, function, instruction.b, line);
            const StraightLineValue rhs =
                GetRKValue(state_values, declarations, function, instruction.c, line);
            expression = RenderBinaryExpression(lhs.text, "<=", rhs.text);
            if (instruction.a != 0) {
                expression = RenderUnaryExpression("not", expression);
            }
            break;
        }
        case Lua51Opcode::kTest: {
            const StraightLineValue value =
                GetRegisterValue(state_values, declarations, function, instruction.a, line);
            expression = value.text;
            if (instruction.c != 0) {
                expression = RenderUnaryExpression("not", expression);
            }
            break;
        }
        case Lua51Opcode::kTestSet: {
            const StraightLineValue value =
                GetRegisterValue(state_values, declarations, function, instruction.b, line);
            expression = value.text;
            if (instruction.c == 0) {
                expression = RenderUnaryExpression("not", expression);
            }
            break;
        }
        default:
            break;
    }
    return expression;
}

void AppendLuaChainedAndConditions(std::string* combined_condition,
                                   int* combined_then_start,
                                   int branch_target,
                                   const std::vector<Lua51Instruction>& instructions,
                                   std::vector<StraightLineValue>* preview_values,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   const LuaInlineRangeEvaluator& evaluate_inline_range) {
    if (combined_condition == nullptr || combined_then_start == nullptr || preview_values == nullptr) {
        return;
    }

    int scan_line = *combined_then_start;
    while (scan_line <= branch_target - 2 &&
           scan_line <= static_cast<int>(instructions.size())) {
        const Lua51Instruction& candidate =
            instructions[static_cast<std::size_t>(scan_line - 1)];
        if (decompiler::IsConditionalBranchOpcode(candidate.opcode)) {
            if (scan_line + 1 > static_cast<int>(instructions.size())) {
                break;
            }

            const Lua51Instruction& candidate_jump =
                instructions[static_cast<std::size_t>(scan_line)];
            if (candidate_jump.opcode != Lua51Opcode::kJmp) {
                break;
            }

            const int candidate_target = decompiler::ComputeJumpTargetLine(
                scan_line + 1, candidate_jump.sbx, static_cast<int>(instructions.size()));
            const int resolved_candidate_target =
                decompiler::ResolveTerminalJumpTarget(instructions, candidate_target);
            if (candidate_target != branch_target &&
                resolved_candidate_target != branch_target) {
                break;
            }

            if (candidate.opcode == Lua51Opcode::kTestSet) {
                break;
            }

            const int candidate_render_line =
                static_cast<int>(candidate.line == 0 ? scan_line : candidate.line);
            const std::string chained_condition = RenderLuaConditionWithValues(
                *preview_values, declarations, function, candidate, candidate_render_line);
            if (chained_condition.empty()) {
                break;
            }

            *combined_condition = RenderLogicalExpression(
                *combined_condition, "and", chained_condition);
            scan_line += 2;
            *combined_then_start = scan_line;
            continue;
        }

        std::string preview_error;
        if (!evaluate_inline_range(scan_line, scan_line, preview_values, &preview_error)) {
            break;
        }
        ++scan_line;
    }
}

}  // namespace refii::lua
