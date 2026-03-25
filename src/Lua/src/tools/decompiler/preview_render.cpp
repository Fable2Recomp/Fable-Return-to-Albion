#include "tools/decompiler/preview_render.h"

#include "tools/decompiler/operation_build.h"
#include "tools/decompiler/operation_render.h"
#include "tools/decompiler/preview_operation_render.h"
#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>
#include <unordered_set>

namespace refii::lua {
namespace {

std::vector<bool> CollectLuaPreviewJoinWrittenRegisters(
    const std::vector<Lua51Instruction>& instructions,
    int register_count,
    int start_line,
    int end_line,
    int statement_line) {
    std::vector<bool> written_registers =
        CollectWrittenLuaRegistersInRange(instructions,
                                          register_count,
                                          statement_line,
                                          statement_line);
    if (start_line > end_line) {
        return written_registers;
    }

    const std::vector<bool> range_written_registers =
        CollectWrittenLuaRegistersInRange(instructions,
                                          register_count,
                                          start_line,
                                          end_line);
    const std::size_t register_limit =
        std::min(written_registers.size(), range_written_registers.size());
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        written_registers[register_index] =
            written_registers[register_index] || range_written_registers[register_index];
    }
    return written_registers;
}

bool RequiresLuaPreviewFullTailRender(const std::vector<Lua51Instruction>& instructions,
                                      int start_line,
                                      int statement_line) {
    if (start_line >= statement_line) {
        return false;
    }

    for (int current = start_line; current < statement_line; ++current) {
        if (current < 1 || static_cast<std::size_t>(current) > instructions.size()) {
            break;
        }

        switch (instructions[static_cast<std::size_t>(current - 1)].opcode) {
            case Lua51Opcode::kNewTable:
            case Lua51Opcode::kSetTable:
            case Lua51Opcode::kSetList:
            case Lua51Opcode::kClosure:
            case Lua51Opcode::kCall:
            case Lua51Opcode::kTailCall:
                return true;
            default:
                break;
        }
    }

    return false;
}

template <typename ErrorBuilder>
bool TryApplyLuaPreviewBuiltOperation(const LuaPreviewRenderContext& context,
                                      int current,
                                      int line,
                                      std::vector<StraightLineValue>* preview_values,
                                      std::vector<bool>* preview_declared,
                                      std::string* preview_error,
                                      int* next_current,
                                      ErrorBuilder&& error_builder) {
    if (preview_values == nullptr || next_current == nullptr) {
        return false;
    }

    std::vector<bool> default_declared;
    if (preview_declared == nullptr) {
        default_declared.resize(static_cast<std::size_t>(context.register_count), false);
        preview_declared = &default_declared;
    }

    const LuaPreviewOperationContext preview_context{
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .values = preview_values,
        .declared = preview_declared,
        .call_arguments_for = context.call_arguments_for,
        .render_child = context.render_child,
    };

    LuaPreviewOperationDisposition disposition = LuaPreviewOperationDisposition::kBuildFailure;
    std::string build_error;
    if (!TryApplyLuaPreviewOperation(
            preview_context,
            current,
            line,
            LuaPreviewOperationMode::kStateTracking,
            &disposition,
            &build_error,
            next_current)) {
        if (!build_error.empty() && preview_error != nullptr) {
            *preview_error = error_builder(build_error);
        } else if (preview_error != nullptr) {
            switch (disposition) {
                case LuaPreviewOperationDisposition::kUnsupportedTailCall:
                    *preview_error = error_builder("tailcall");
                    break;
                case LuaPreviewOperationDisposition::kUnsupportedCallShape:
                    *preview_error = error_builder("call_shape");
                    break;
                case LuaPreviewOperationDisposition::kUnsupportedOpcode:
                case LuaPreviewOperationDisposition::kUnsupportedSideEffect:
                    *preview_error = error_builder("opcode");
                    break;
                case LuaPreviewOperationDisposition::kApplied:
                case LuaPreviewOperationDisposition::kBuildFailure:
                    break;
            }
        }
        return false;
    }
    return true;
}

}  // namespace

bool EvaluateLuaInlineRange(const LuaPreviewRenderContext& context,
                            int start_line,
                            int end_line,
                            std::vector<StraightLineValue>* preview_values,
                            std::vector<bool>* preview_declared,
                            std::string* preview_error) {
    if (start_line > end_line) {
        return true;
    }

    for (int current = start_line; current <= end_line; ++current) {
        if (current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
            if (preview_error != nullptr) {
                *preview_error = "invalid_preview_line";
            }
            return false;
        }

        const Lua51Instruction& instruction =
            context.instructions[static_cast<std::size_t>(current - 1)];
        const int line = static_cast<int>(instruction.line == 0 ? current : instruction.line);
        if (instruction.opcode == Lua51Opcode::kLoadBool &&
            instruction.c != 0 &&
            !IsLoadBoolPair(context.instructions, current)) {
            if (preview_error != nullptr) {
                *preview_error = "unsupported_preview_loadbool_skip";
            }
            return false;
        }
        int next_current = current + 1;
        if (TryApplyLuaPreviewBuiltOperation(
                context,
                current,
                line,
                preview_values,
                preview_declared,
                preview_error,
                &next_current,
                [](std::string_view reason) {
                    if (reason == "tailcall") {
                        return std::string("unsupported_preview_tailcall");
                    }
                    if (reason == "call_shape") {
                        return std::string("unsupported_preview_call_shape");
                    }
                    if (reason == "unsupported_nested_closure") {
                        return std::string("unsupported_preview_nested_closure");
                    }
                    if (reason == "invalid_constant_index") {
                        return std::string("invalid_preview_constant_index");
                    }
                    if (reason == "invalid_global_name") {
                        return std::string("invalid_preview_global_name");
                    }
                    if (reason == "opcode") {
                        return std::string("unsupported_preview_opcode");
                    }
                    return std::string(reason);
                })) {
            current = next_current - 1;
            continue;
        }
        switch (instruction.opcode) {
            case Lua51Opcode::kLoadBool:
                if (instruction.c != 0) {
                    ++current;
                }
                break;
            case Lua51Opcode::kSetList:
                if (instruction.c == 0) {
                    ++current;
                }
                break;
            case Lua51Opcode::kClose:
                break;
            default:
                if (preview_error != nullptr) {
                    *preview_error = "unsupported_preview_opcode";
                }
                return false;
        }
    }

    return true;
}

bool EvaluateLuaBranchPathToJoin(const LuaPreviewRenderContext& context,
                                 int start_line,
                                 int join_line,
                                 std::vector<StraightLineValue>* preview_values,
                                 std::vector<bool>* preview_declared,
                                 std::string* preview_error) {
    if (preview_values == nullptr) {
        return false;
    }
    if (start_line < 1 || join_line < start_line ||
        static_cast<std::size_t>(join_line) > context.instructions.size()) {
        if (preview_error != nullptr) {
            *preview_error = "invalid_branch_preview_range";
        }
        return false;
    }

    std::unordered_set<int> visited_lines;
    int current = start_line;
    int steps = 0;
    const int max_steps = std::max<int>(16, join_line - start_line + 4);

    while (current < join_line) {
        if (current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
            if (preview_error != nullptr) {
                *preview_error = "invalid_branch_preview_line";
            }
            return false;
        }
        if (!visited_lines.insert(current).second || ++steps > max_steps) {
            if (preview_error != nullptr) {
                *preview_error = "branch_preview_cycle";
            }
            return false;
        }

        const Lua51Instruction& instruction =
            context.instructions[static_cast<std::size_t>(current - 1)];
        const int line = static_cast<int>(instruction.line == 0 ? current : instruction.line);
        int next_current = current + 1;

        switch (instruction.opcode) {
            case Lua51Opcode::kJmp: {
                const int target = decompiler::ComputeJumpTargetLine(
                    current, instruction.sbx, static_cast<int>(context.instructions.size()));
                if (target < current + 1 || target > join_line) {
                    if (preview_error != nullptr) {
                        *preview_error = "unsupported_branch_preview_jump";
                    }
                    return false;
                }
                current = target;
                continue;
            }
            case Lua51Opcode::kClose:
                ++current;
                continue;
            case Lua51Opcode::kTest: {
                if (current + 1 > static_cast<int>(context.instructions.size()) ||
                    context.instructions[static_cast<std::size_t>(current)].opcode != Lua51Opcode::kJmp) {
                    if (preview_error != nullptr) {
                        *preview_error = "unsupported_branch_preview_test_shape";
                    }
                    return false;
                }

                const Lua51Instruction& jump =
                    context.instructions[static_cast<std::size_t>(current)];
                const int target = decompiler::ComputeJumpTargetLine(
                    current + 1, jump.sbx, static_cast<int>(context.instructions.size()));
                if (target == current + 3 &&
                    current + 2 <= static_cast<int>(context.instructions.size())) {
                    const Lua51Instruction& fallback =
                        context.instructions[static_cast<std::size_t>(current + 1)];
                    if (fallback.opcode == Lua51Opcode::kLoadNil &&
                        fallback.a == instruction.a &&
                        fallback.b == instruction.a) {
                        current = target;
                        continue;
                    }
                }

                if (preview_error != nullptr) {
                    *preview_error = "unsupported_branch_preview_conditional";
                }
                return false;
            }
            default:
                if (TryApplyLuaPreviewBuiltOperation(
                        context,
                        current,
                        line,
                        preview_values,
                        preview_declared,
                        preview_error,
                        &next_current,
                        [&](std::string_view reason) {
                            if (reason == "tailcall") {
                                return std::string("unsupported_branch_preview_tailcall");
                            }
                            if (reason == "call_shape") {
                                return std::string("unsupported_branch_preview_call_shape");
                            }
                            if (reason == "unsupported_nested_closure") {
                                return std::string("unsupported_branch_preview_nested_closure");
                            }
                            if (reason == "invalid_constant_index") {
                                return std::string("invalid_branch_preview_constant_index");
                            }
                            if (reason == "invalid_global_name") {
                                return std::string("invalid_branch_preview_global_name");
                            }
                            if (reason == "opcode") {
                                return std::string("unsupported_branch_preview_opcode");
                            }
                            return std::string(reason);
                        })) {
                    current = next_current;
                    continue;
                }
                if (preview_error != nullptr) {
                    *preview_error = "unsupported_branch_preview_opcode";
                }
                return false;
        }
    }

    return current == join_line;
}

bool TryBuildLuaPreviewStatementJoin(const LuaPreviewRenderContext& context,
                                     const std::vector<StraightLineValue>& base_values,
                                     const std::vector<bool>& base_declared,
                                     int then_start_line,
                                     int then_end_line,
                                     int else_start_line,
                                     int else_end_line,
                                     int statement_line,
                                     LuaPreviewStatementJoin* join,
                                     std::string* preview_error) {
    if (join == nullptr || statement_line < 1 ||
        static_cast<std::size_t>(statement_line) > context.instructions.size()) {
        return false;
    }

    join->then_values = base_values;
    join->else_values = base_values;
    join->then_declared = base_declared;
    join->else_declared = base_declared;
    join->then_written_registers = CollectLuaPreviewJoinWrittenRegisters(
        context.instructions,
        context.register_count,
        then_start_line,
        then_end_line,
        statement_line);
    join->else_written_registers = CollectLuaPreviewJoinWrittenRegisters(
        context.instructions,
        context.register_count,
        else_start_line,
        else_end_line,
        statement_line);
    join->then_requires_full_tail_render =
        RequiresLuaPreviewFullTailRender(context.instructions,
                                         then_start_line,
                                         statement_line);
    join->else_requires_full_tail_render =
        RequiresLuaPreviewFullTailRender(context.instructions,
                                         else_start_line,
                                         statement_line);
    join->statement_line = statement_line;

    if (then_start_line <= then_end_line &&
        !EvaluateLuaInlineRange(
            context,
            then_start_line,
            then_end_line,
            &join->then_values,
            &join->then_declared,
            preview_error)) {
        return false;
    }
    FinalizeLuaJoinCandidateState(&join->then_values,
                                  &join->then_declared,
                                  base_values,
                                  base_declared,
                                  join->then_written_registers,
                                  context.declarations,
                                  context.function,
                                  statement_line,
                                  context.register_count);
    if (else_start_line <= else_end_line &&
        !EvaluateLuaInlineRange(
            context,
            else_start_line,
            else_end_line,
            &join->else_values,
            &join->else_declared,
            preview_error)) {
        return false;
    }
    FinalizeLuaJoinCandidateState(&join->else_values,
                                  &join->else_declared,
                                  base_values,
                                  base_declared,
                                  join->else_written_registers,
                                  context.declarations,
                                  context.function,
                                  statement_line,
                                  context.register_count);
    return true;
}

bool TryBuildLuaPreviewStatementJoinToLine(const LuaPreviewRenderContext& context,
                                           const std::vector<StraightLineValue>& base_values,
                                           const std::vector<bool>& base_declared,
                                           int then_start_line,
                                           int else_start_line,
                                           int join_line,
                                           LuaPreviewStatementJoin* join,
                                           std::string* preview_error) {
    if (join == nullptr || join_line < 1 ||
        static_cast<std::size_t>(join_line) > context.instructions.size()) {
        return false;
    }

    join->then_values = base_values;
    join->else_values = base_values;
    join->then_declared = base_declared;
    join->else_declared = base_declared;
    join->then_written_registers = CollectLuaPreviewJoinWrittenRegisters(
        context.instructions,
        context.register_count,
        then_start_line,
        join_line - 1,
        join_line);
    join->else_written_registers = CollectLuaPreviewJoinWrittenRegisters(
        context.instructions,
        context.register_count,
        else_start_line,
        join_line - 1,
        join_line);
    join->then_requires_full_tail_render =
        RequiresLuaPreviewFullTailRender(context.instructions,
                                         then_start_line,
                                         join_line);
    join->else_requires_full_tail_render =
        RequiresLuaPreviewFullTailRender(context.instructions,
                                         else_start_line,
                                         join_line);
    join->statement_line = join_line;

    if (!EvaluateLuaBranchPathToJoin(
            context,
            then_start_line,
            join_line,
            &join->then_values,
            &join->then_declared,
            preview_error) ||
        !EvaluateLuaBranchPathToJoin(
            context,
            else_start_line,
            join_line,
            &join->else_values,
            &join->else_declared,
            preview_error)) {
        return false;
    }
    FinalizeLuaJoinCandidateState(&join->then_values,
                                  &join->then_declared,
                                  base_values,
                                  base_declared,
                                  join->then_written_registers,
                                  context.declarations,
                                  context.function,
                                  join_line,
                                  context.register_count);
    FinalizeLuaJoinCandidateState(&join->else_values,
                                  &join->else_declared,
                                  base_values,
                                  base_declared,
                                  join->else_written_registers,
                                  context.declarations,
                                  context.function,
                                  join_line,
                                  context.register_count);
    return true;
}

bool TryRenderLuaPreviewStatementJoin(const LuaPreviewRenderContext& context,
                                      const LuaPreviewStatementJoin& join,
                                      std::string* then_statement,
                                      std::string* else_statement) {
    return RenderLuaSingleStatementFromPreview(
               context,
               join.then_values,
               &join.then_declared,
               join.statement_line,
               then_statement) &&
           RenderLuaSingleStatementFromPreview(
               context,
               join.else_values,
               &join.else_declared,
               join.statement_line,
               else_statement);
}

bool TryBuildLuaPreviewStatementJoinStates(const LuaPreviewRenderContext& context,
                                           const LuaPreviewStatementJoin& join,
                                           LuaBranchStateSnapshot* then_state,
                                           LuaBranchStateSnapshot* else_state) {
    if (then_state == nullptr || else_state == nullptr ||
        join.statement_line < 1 ||
        static_cast<std::size_t>(join.statement_line) > context.instructions.size()) {
        return false;
    }

    const auto apply_join_state = [&](const std::vector<StraightLineValue>& source_values,
                                      const std::vector<bool>& source_declared,
                                      const std::vector<bool>& written_registers,
                                      LuaBranchStateSnapshot* state) {
        state->values = source_values;
        state->declared = source_declared;

        const Lua51Instruction& statement_instruction =
            context.instructions[static_cast<std::size_t>(join.statement_line - 1)];
        const int render_line = static_cast<int>(
            statement_instruction.line == 0 ? join.statement_line : statement_instruction.line);
        const LuaPreviewOperationContext preview_context{
            .instructions = context.instructions,
            .declarations = context.declarations,
            .function = context.function,
            .register_count = context.register_count,
            .values = &state->values,
            .declared = &state->declared,
            .call_arguments_for = context.call_arguments_for,
            .render_child = context.render_child,
        };
        LuaPreviewOperationDisposition disposition = LuaPreviewOperationDisposition::kBuildFailure;
        std::string build_error;
        int next_current = join.statement_line + 1;
        if (!TryApplyLuaPreviewOperation(preview_context,
                                         join.statement_line,
                                         render_line,
                                         LuaPreviewOperationMode::kStateTracking,
                                         &disposition,
                                         &build_error,
                                         &next_current)) {
            return false;
        }
        FinalizeLuaJoinCandidateState(&state->values,
                                      &state->declared,
                                      source_values,
                                      source_declared,
                                      written_registers,
                                      context.declarations,
                                      context.function,
                                      render_line,
                                      context.register_count);
        return true;
    };

    return apply_join_state(
               join.then_values, join.then_declared, join.then_written_registers, then_state) &&
           apply_join_state(
               join.else_values, join.else_declared, join.else_written_registers, else_state);
}

bool IsLuaPreviewRangeSideEffectFree(const LuaPreviewRenderContext& context,
                                     int start_line,
                                     int end_line) {
    if (start_line > end_line) {
        return true;
    }
    if (start_line < 1 || static_cast<std::size_t>(end_line) > context.instructions.size()) {
        return false;
    }
    std::vector<StraightLineValue> preview_values(static_cast<std::size_t>(context.register_count));
    std::vector<bool> preview_declared(static_cast<std::size_t>(context.register_count), false);
    for (int line = start_line; line <= end_line; ++line) {
        const Lua51Instruction& preview_instruction =
            context.instructions[static_cast<std::size_t>(line - 1)];
        if (preview_instruction.opcode == Lua51Opcode::kClose) {
            continue;
        }
        if (preview_instruction.opcode == Lua51Opcode::kLoadBool &&
            preview_instruction.c != 0 &&
            !IsLoadBoolPair(context.instructions, line)) {
            return false;
        }
        const LuaPreviewOperationContext preview_context{
            .instructions = context.instructions,
            .declarations = context.declarations,
            .function = context.function,
            .register_count = context.register_count,
            .values = &preview_values,
            .declared = &preview_declared,
            .call_arguments_for = context.call_arguments_for,
            .render_child = context.render_child,
        };
        LuaPreviewOperationDisposition disposition = LuaPreviewOperationDisposition::kBuildFailure;
        std::string build_error;
        int next_current = line + 1;
        if (!TryApplyLuaPreviewOperation(preview_context,
                line,
                line,
                LuaPreviewOperationMode::kSideEffectFreeOnly,
                &disposition,
                &build_error,
                &next_current)) {
            return false;
        }
        line = next_current - 1;
    }
    return true;
}

bool RenderLuaSingleStatementFromPreview(const LuaPreviewRenderContext& context,
                                         const std::vector<StraightLineValue>& state_values,
                                         const std::vector<bool>* state_declared,
                                         int statement_line,
                                         std::string* statement) {
    if (statement == nullptr || statement_line < 1 ||
        static_cast<std::size_t>(statement_line) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& statement_instruction =
        context.instructions[static_cast<std::size_t>(statement_line - 1)];
    const int render_line = static_cast<int>(
        statement_instruction.line == 0 ? statement_line : statement_instruction.line);
    std::vector<StraightLineValue> preview_values = state_values;
    const LuaOperationBuildContext build_context{
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .values = &preview_values,
        .declared = state_declared,
        .call_arguments_for = context.call_arguments_for,
        .return_values_for = nullptr,
        .setlist_values_for = nullptr,
        .render_child = context.render_child,
        .resolve_setlist_block = nullptr,
        .trace_render = false,
        .trace = {},
    };
    LuaBuiltOperation built_operation;
    std::string build_error;
    if (!TryBuildLuaBuiltOperation(build_context,
                                   statement_line,
                                   statement_line,
                                   statement_line,
                                   render_line,
                                   &build_error,
                                   &built_operation)) {
        return false;
    }

    const LuaOperationRenderContext render_context{
        .lines = nullptr,
        .values = nullptr,
        .declared = nullptr,
        .declarations = context.declarations,
        .function = context.function,
    };
    return TryRenderLuaBuiltOperationStatement(render_context, built_operation, statement);
}

}  // namespace refii::lua
