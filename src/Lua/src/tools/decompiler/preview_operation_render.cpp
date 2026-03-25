#include "tools/decompiler/preview_operation_render.h"

namespace refii::lua {

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
        .setlist_values_for = nullptr,
        .render_child = context.render_child,
        .resolve_setlist_block = nullptr,
        .trace_render = false,
        .trace = {},
        .allow_opaque_preview_closures = true,
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

    *next_current = built_operation.next_current;

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
                                  std::is_same_v<T, LuaSelfOperation>) {
                        return true;
                    } else if constexpr (std::is_same_v<T, LuaCallOperation>) {
                        if (payload.tail_return) {
                            *disposition = LuaPreviewOperationDisposition::kUnsupportedTailCall;
                            return false;
                        }
                        if (payload.result_mode == LuaCallResultMode::kDiscard) {
                            *disposition = LuaPreviewOperationDisposition::kUnsupportedCallShape;
                            return false;
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
    *disposition = LuaPreviewOperationDisposition::kApplied;
    return true;
}

}  // namespace refii::lua
