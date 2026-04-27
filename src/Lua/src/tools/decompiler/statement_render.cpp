#include "tools/decompiler/statement_render.h"

#include "tools/decompiler/operation_build.h"
#include "tools/decompiler/operation_render.h"

namespace refii::lua {
namespace {

LuaStatementRenderResult ErrorResult(std::string* error, std::string message) {
    if (error != nullptr) {
        *error = std::move(message);
    }
    return LuaStatementRenderResult{.disposition = LuaStatementRenderDisposition::kError};
}

LuaStatementRenderResult ContinueResult(int next_current) {
    return LuaStatementRenderResult{
        .disposition = LuaStatementRenderDisposition::kContinue,
        .next_current = next_current,
    };
}

LuaStatementRenderResult ReturnResult() {
    return LuaStatementRenderResult{
        .disposition = LuaStatementRenderDisposition::kReturn,
        .next_current = -1,
    };
}

}  // namespace

LuaStatementRenderResult TryRenderLuaStraightLineStatement(
    const LuaStatementRenderContext& context,
    int start_line,
    int end_line,
    int current,
    int line,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.call_arguments_for || !context.return_values_for ||
        !context.setlist_values_for || !context.render_child ||
        !context.resolve_setlist_block) {
        return ErrorResult(error, "statement_context_missing");
    }

    auto& lines = *context.lines;
    auto& values = *context.values;
    auto& declared = *context.declared;
    const auto& instructions = context.instructions;
    const auto& declaration_map = context.declarations;
    const auto& function = context.function;
    const int register_count = context.register_count;
    const LuaOperationRenderContext operation_context{
        .lines = &lines,
        .values = &values,
        .declared = &declared,
        .declarations = declaration_map,
        .function = function,
    };
    const LuaOperationBuildContext build_context{
        .instructions = instructions,
        .declarations = declaration_map,
        .function = function,
        .register_count = register_count,
        .values = &values,
        .declared = &declared,
        .call_arguments_for = context.call_arguments_for,
        .return_values_for = context.return_values_for,
        .setlist_values_for = context.setlist_values_for,
        .render_child = context.render_child,
        .resolve_setlist_block = context.resolve_setlist_block,
        .trace_render = context.trace_render,
        .trace = context.trace,
        .allow_opaque_nested_closure_fallback = true,
    };

    if (current < 1 || static_cast<std::size_t>(current) > instructions.size()) {
        return ErrorResult(error, "invalid_statement_line");
    }

    const Lua51Instruction& instruction = instructions[static_cast<std::size_t>(current - 1)];

    {
        LuaBuiltOperation built_operation;
        std::string build_error;
        if (TryBuildLuaBuiltOperation(
                build_context,
                start_line,
                end_line,
                current,
                line,
                &build_error,
                &built_operation)) {
            if (ApplyLuaBuiltOperation(operation_context, built_operation)) {
                return ReturnResult();
            }
            return ContinueResult(built_operation.next_current);
        }
        if (!build_error.empty()) {
            return ErrorResult(error, std::move(build_error));
        }
    }

    switch (instruction.opcode) {
        case Lua51Opcode::kClose:
            break;
        default:
            return ErrorResult(error,
                               "unsupported_opcode@pc=" + std::to_string(current) + ",opcode=" +
                                   std::string(DescribeLua51Opcode(instruction.opcode)));
    }

    return ContinueResult(current + 1);
}

}  // namespace refii::lua
