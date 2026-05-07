#include "tools/decompiler/preview_operation_render.h"
#include "tools/decompiler/condition_render.h"

#include <algorithm>
#include <cctype>
#include <string>

namespace refii::lua {
namespace {

bool ShouldUseLuaCanonicalLoadBoolSkip(const Lua51Function& function) {
    (void)function;
    // LOADBOOL with C != 0 always skips the following instruction in Lua 5.1.
    return true;
}

void ApplyLuaPreviewDiscardCallBarrier(const LuaPreviewOperationContext& context,
                                       const Lua51Instruction& instruction) {
    if (context.values == nullptr || context.register_count <= 0 || instruction.a < 0) {
        return;
    }

    const int register_limit = std::min(context.register_count,
                                        static_cast<int>(context.values->size()));
    if (register_limit <= 0 || instruction.a >= register_limit) {
        return;
    }

    const int touched_register_begin = std::max(0, instruction.a);
    if (touched_register_begin >= register_limit) {
        return;
    }

    int touched_register_end = touched_register_begin;
    // Fixed-arg discard calls invalidate their full fixed argument window.
    // Open-arg calls (B == 0) stay conservative at the base receiver register.
    if (instruction.b > 0) {
        touched_register_end =
            std::min(register_limit - 1, instruction.a + instruction.b - 1);
    }

    for (int reg = touched_register_begin; reg <= touched_register_end; ++reg) {
        (*context.values)[static_cast<std::size_t>(reg)] = StraightLineValue{};
    }
}

}  // namespace

bool TryApplyLuaPreviewOperation(const LuaPreviewOperationContext& context,
                                 int current,
                                 int line,
                                 LuaPreviewOperationMode mode,
                                 LuaPreviewOperationDisposition* disposition,
                                 std::string* build_error,
                                 int* next_current) {
    if (disposition == nullptr || build_error == nullptr || next_current == nullptr ||
        context.values == nullptr || context.declared == nullptr) {
        return false;
    }

    *disposition = LuaPreviewOperationDisposition::kBuildFailure;
    build_error->clear();

    const LuaOperationBuildContext build_context{
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .values = context.values,
        .declared = context.declared,
        .call_arguments_for = context.call_arguments_for,
        .return_values_for = nullptr,
        .setlist_values_for = context.setlist_values_for,
        .render_child = context.render_child,
        .resolve_setlist_block = context.resolve_setlist_block,
        .trace_render = false,
        .trace = {},
        .allow_opaque_preview_closures = true,
        .allow_cross_line_shortcuts = true,
    };

    LuaBuiltOperation built_operation;
    if (!TryBuildLuaBuiltOperation(
            build_context,
            current,
            current,
            current,
            line,
            build_error,
            &built_operation)) {
        return false;
    }

    const Lua51Instruction& instruction =
        context.instructions[static_cast<std::size_t>(current - 1)];
    *next_current = built_operation.next_current;
    bool apply_discard_call_barrier = false;

    switch (mode) {
        case LuaPreviewOperationMode::kSideEffectFreeOnly:
            if (built_operation.stop_render ||
                !IsLuaBuiltOperationSideEffectFree(built_operation)) {
                *disposition = LuaPreviewOperationDisposition::kUnsupportedSideEffect;
                return false;
            }
            break;
        case LuaPreviewOperationMode::kStateTracking: {
            const bool supported = std::visit(
                [&](const auto& payload) -> bool {
                    using T = std::decay_t<decltype(payload)>;
                    if constexpr (std::is_same_v<T, std::monostate> ||
                                  std::is_same_v<T, LuaAssignmentOperation> ||
                                  std::is_same_v<T, LuaValueOperation> ||
                                  std::is_same_v<T, LuaSelfOperation> ||
                                  std::is_same_v<T, LuaGlobalSetOperation> ||
                                  std::is_same_v<T, LuaUpvalueSetOperation> ||
                                  std::is_same_v<T, LuaTableSetOperation> ||
                                  std::is_same_v<T, LuaSetListOperation>) {
                        return true;
                    } else if constexpr (std::is_same_v<T, LuaCallOperation>) {
                        if (payload.tail_return) {
                            *disposition = LuaPreviewOperationDisposition::kUnsupportedTailCall;
                            return false;
                        }
                        if (payload.result_mode == LuaCallResultMode::kDiscard) {
                            apply_discard_call_barrier = true;
                            return true;
                        }
                        return true;
                    } else {
                        *disposition = LuaPreviewOperationDisposition::kUnsupportedOpcode;
                        return false;
                    }
                },
                built_operation.payload);
            if (!supported) {
                return false;
            }
            break;
        }
    }

    const LuaOperationRenderContext render_context{
        .lines = nullptr,
        .values = context.values,
        .declared = context.declared,
        .declarations = context.declarations,
        .function = context.function,
    };
    ApplyLuaBuiltOperation(render_context, built_operation);
    if (mode == LuaPreviewOperationMode::kStateTracking) {
        std::visit(
            [&](const auto& payload) {
                using T = std::decay_t<decltype(payload)>;
                if constexpr (std::is_same_v<T, LuaSetListOperation>) {
                    if (context.values != nullptr &&
                        payload.target_reg >= 0 &&
                        static_cast<std::size_t>(payload.target_reg) < context.values->size()) {
                        StraightLineValue& current_value =
                            (*context.values)[static_cast<std::size_t>(payload.target_reg)];
                        if (!current_value.valid) {
                            current_value =
                                StraightLineValue{payload.table_expression, true, false};
                        }
                    }
                }
            },
            built_operation.payload);
    }
    if (instruction.opcode == Lua51Opcode::kLoadBool &&
        instruction.c != 0 &&
        *next_current <= current + 1) {
        const int instruction_count = static_cast<int>(context.instructions.size());
        *next_current = std::min(instruction_count + 1, current + 2);
    }
    if (apply_discard_call_barrier) {
        ApplyLuaPreviewDiscardCallBarrier(context, instruction);
    }
    *disposition = LuaPreviewOperationDisposition::kApplied;
    return true;
}

}  // namespace refii::lua
