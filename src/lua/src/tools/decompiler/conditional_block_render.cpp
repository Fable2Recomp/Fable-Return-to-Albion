#include "tools/decompiler/conditional_block_render.h"

#include "tools/decompiler/preview_join_render.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <cctype>

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

bool TryExtractIfThenCondition(std::string_view line, std::string* condition_out) {
    if (condition_out == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then") || trimmed.size() <= 8) {
        return false;
    }

    *condition_out = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
    return !condition_out->empty();
}

std::string ExtractLineIndent(std::string_view line) {
    std::size_t index = 0;
    while (index < line.size() &&
           std::isspace(static_cast<unsigned char>(line[index])) != 0) {
        ++index;
    }
    return std::string(line.substr(0, index));
}

bool TryFoldElseIntoElseIfChain(std::vector<std::string>* lines,
                                std::size_t else_header_line,
                                std::size_t else_body_start,
                                std::size_t else_body_end_exclusive) {
    if (lines == nullptr || else_header_line >= lines->size() ||
        else_body_start >= else_body_end_exclusive ||
        else_body_end_exclusive > lines->size()) {
        return false;
    }

    std::size_t first_nonempty = lines->size();
    for (std::size_t index = else_body_start; index < else_body_end_exclusive; ++index) {
        if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[index])).empty()) {
            first_nonempty = index;
            break;
        }
    }
    if (first_nonempty != else_body_start || first_nonempty >= lines->size()) {
        return false;
    }

    std::size_t last_nonempty = lines->size();
    for (std::size_t index = else_body_end_exclusive; index > else_body_start;) {
        --index;
        if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[index])).empty()) {
            last_nonempty = index;
            break;
        }
    }
    if (last_nonempty == lines->size() || last_nonempty <= first_nonempty) {
        return false;
    }

    const std::string_view trailing = TrimLuaWhitespace(StripLuaLineComment((*lines)[last_nonempty]));
    if (trailing != "end") {
        return false;
    }

    std::string condition;
    if (!TryExtractIfThenCondition((*lines)[first_nonempty], &condition)) {
        return false;
    }

    const std::string branch_indent = ExtractLineIndent((*lines)[else_header_line]);
    (*lines)[first_nonempty] = branch_indent + "elseif " + condition + " then";
    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(last_nonempty));
    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(else_header_line));
    return true;
}

bool DoesLuaConditionalInstructionReadRegister(const Lua51Instruction& instruction, int reg) {
    switch (instruction.opcode) {
        case Lua51Opcode::kMove:
        case Lua51Opcode::kUnm:
        case Lua51Opcode::kNot:
        case Lua51Opcode::kLen:
        case Lua51Opcode::kSetGlobal:
        case Lua51Opcode::kSetUpval:
        case Lua51Opcode::kTest:
            return instruction.a == reg || instruction.b == reg;
        case Lua51Opcode::kGetTable:
        case Lua51Opcode::kSelf:
        case Lua51Opcode::kAdd:
        case Lua51Opcode::kSub:
        case Lua51Opcode::kMul:
        case Lua51Opcode::kDiv:
        case Lua51Opcode::kMod:
        case Lua51Opcode::kPow:
        case Lua51Opcode::kEq:
        case Lua51Opcode::kLt:
        case Lua51Opcode::kLe:
            return instruction.b == reg || instruction.c == reg;
        case Lua51Opcode::kSetTable:
            return instruction.a == reg || instruction.b == reg || instruction.c == reg;
        case Lua51Opcode::kConcat:
            return reg >= instruction.b && reg <= instruction.c;
        case Lua51Opcode::kTestSet:
            return instruction.b == reg;
        case Lua51Opcode::kCall:
        case Lua51Opcode::kTailCall:
            if (reg < instruction.a) {
                return false;
            }
            if (instruction.b == 0) {
                return true;
            }
            return reg < instruction.a + instruction.b;
        case Lua51Opcode::kReturn:
            if (reg < instruction.a) {
                return false;
            }
            if (instruction.b == 0) {
                return true;
            }
            return reg < instruction.a + instruction.b - 1;
        case Lua51Opcode::kSetList:
            if (instruction.a == reg) {
                return true;
            }
            if (instruction.b == 0) {
                return reg > instruction.a;
            }
            return reg > instruction.a && reg <= instruction.a + instruction.b;
        default:
            return false;
    }
}

bool IsLuaConditionalRegisterReadBeforeWrite(
    const LuaConditionalBlockRenderContext& context,
    int reg,
    int start_line) {
    if (reg < 0 || start_line < 1) {
        return false;
    }
    const int instruction_count = static_cast<int>(context.instructions.size());
    for (int line = start_line; line <= instruction_count; ++line) {
        const Lua51Instruction& instruction =
            context.instructions[static_cast<std::size_t>(line - 1)];
        if (DoesLuaConditionalInstructionReadRegister(instruction, reg)) {
            return true;
        }
        const std::vector<bool> written_registers =
            context.collect_written_registers_in_range(line, line);
        if (static_cast<std::size_t>(reg) < written_registers.size() &&
            written_registers[static_cast<std::size_t>(reg)]) {
            return false;
        }
    }
    return false;
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
    const auto has_meaningful_name = [&](int reg, int line) {
        const std::string name =
            GetStraightLineRegisterName(context.declarations, context.function, reg, line);
        return !name.empty() && !IsRenderTemporaryName(name);
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

    if (!block.has_else && lines.size() == then_lines_start) {
        const std::vector<bool> then_written_registers =
            context.collect_written_registers_in_range(block.then_start, block.then_end);
        const std::vector<bool> else_written_registers(static_cast<std::size_t>(
            context.register_count), false);
        int changed_register = -1;
        StraightLineValue merged_value;
        if (TryBuildLuaPreviewSingleRegisterJoinExpression(condition,
                                                           then_end_values,
                                                           then_end_declared,
                                                           then_written_registers,
                                                           saved_state.values,
                                                           saved_state.declared,
                                                           else_written_registers,
                                                           context.declarations,
                                                           context.function,
                                                           block.after_line,
                                                           context.register_count,
                                                           &changed_register,
                                                           &merged_value) &&
            changed_register >= 0 &&
            !has_meaningful_name(changed_register, block.after_line)) {
            if (IsLuaConditionalRegisterReadBeforeWrite(context,
                                                        changed_register,
                                                        block.after_line)) {
                const std::string register_name =
                    GetStraightLineRegisterName(context.declarations,
                                                context.function,
                                                changed_register,
                                                block.after_line);
                const StraightLineValue& then_value =
                    then_end_values[static_cast<std::size_t>(changed_register)];
                if (!register_name.empty() && then_value.valid &&
                    !then_value.multiple_results) {
                    AppendIndentedStatement(
                        &lines,
                        "  ",
                        register_name + " = " + then_value.text);
                }
            }
        }
    }

    if (!block.has_else && lines.size() == then_lines_start) {
        const std::vector<bool> then_written_registers =
            context.collect_written_registers_in_range(block.then_start, block.then_end);
        const std::vector<bool> else_written_registers(static_cast<std::size_t>(
            context.register_count), false);
        int changed_register = -1;
        StraightLineValue merged_value;
        if (TryBuildLuaPreviewSingleRegisterJoinExpression(condition,
                                                           then_end_values,
                                                           then_end_declared,
                                                           then_written_registers,
                                                           saved_state.values,
                                                           saved_state.declared,
                                                           else_written_registers,
                                                           context.declarations,
                                                           context.function,
                                                           block.after_line,
                                                           context.register_count,
                                                           &changed_register,
                                                           &merged_value) &&
            changed_register >= 0 &&
            !has_meaningful_name(changed_register, block.after_line)) {
            lines.pop_back();
            restore_state(saved_state);
            values[static_cast<std::size_t>(changed_register)] = std::move(merged_value);
            declared[static_cast<std::size_t>(changed_register)] =
                then_end_declared[static_cast<std::size_t>(changed_register)];
            if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
                return ReturnResult();
            }
            return ContinueResult(block.after_line);
        }
    }

    if (block.has_else) {
        const std::size_t else_header_line = lines.size();
        AppendIndentedStatement(&lines, "  ", "else");
        const std::size_t else_body_start = lines.size();
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
        const std::size_t else_body_end_exclusive = lines.size();
        if (block.prefer_elseif_chain) {
            TryFoldElseIntoElseIfChain(&lines,
                                       else_header_line,
                                       else_body_start,
                                       else_body_end_exclusive);
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
