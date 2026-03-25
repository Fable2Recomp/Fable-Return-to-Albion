#include "tools/decompiler/operation_render.h"

#include "tools/decompiler/rendered_lua_text.h"

namespace refii::lua {

void ApplyLuaAssignmentOperation(const LuaOperationRenderContext& context,
                                 const LuaAssignmentOperation& operation) {
    if (context.values == nullptr || context.declared == nullptr) {
        return;
    }

    if (context.lines == nullptr) {
        if (operation.count <= 1) {
            if (operation.first_reg >= 0 &&
                static_cast<std::size_t>(operation.first_reg) < context.values->size()) {
                (*context.values)[static_cast<std::size_t>(operation.first_reg)] =
                    StraightLineValue{operation.expression, true, operation.multiple_results};
                if (operation.first_reg >= context.function.parameter_count) {
                    (*context.declared)[static_cast<std::size_t>(operation.first_reg)] = true;
                }
            }
            return;
        }

        for (int offset = 0; offset < operation.count; ++offset) {
            const int reg = operation.first_reg + offset;
            if (reg < 0 || static_cast<std::size_t>(reg) >= context.values->size()) {
                continue;
            }

            if (offset == 0) {
                (*context.values)[static_cast<std::size_t>(reg)] =
                    StraightLineValue{operation.expression, true, operation.multiple_results};
            } else {
                const std::string name = GetStraightLineRegisterName(
                    context.declarations, context.function, reg, operation.line);
                (*context.values)[static_cast<std::size_t>(reg)] =
                    StraightLineValue{name, true, false};
            }

            if (reg >= context.function.parameter_count) {
                (*context.declared)[static_cast<std::size_t>(reg)] = true;
            }
        }
        return;
    }

    if (operation.count <= 1) {
        AssignRegister(context.lines,
                       context.values,
                       context.declared,
                       context.declarations,
                       context.function,
                       operation.first_reg,
                       operation.line,
                       operation.expression,
                       operation.multiple_results);
        return;
    }

    AssignRegisters(context.lines,
                    context.values,
                    context.declared,
                    context.declarations,
                    context.function,
                    operation.first_reg,
                    operation.count,
                    operation.line,
                    operation.expression);
}

void ApplyLuaValueOperation(const LuaOperationRenderContext& context,
                            const LuaValueOperation& operation) {
    if (context.values == nullptr || operation.reg < 0 ||
        static_cast<std::size_t>(operation.reg) >= context.values->size()) {
        return;
    }

    (*context.values)[static_cast<std::size_t>(operation.reg)] =
        StraightLineValue{operation.expression, true, operation.multiple_results};
}

void ApplyLuaStatementOperation(const LuaOperationRenderContext& context,
                                const LuaStatementOperation& operation) {
    if (context.lines == nullptr) {
        return;
    }
    AppendIndentedStatement(context.lines, "  ", operation.statement);
}

void ApplyLuaSelfOperation(const LuaOperationRenderContext& context,
                           const LuaSelfOperation& operation) {
    ApplyLuaValueOperation(
        context,
        LuaValueOperation{
            .reg = operation.object_reg,
            .expression = operation.object_expression,
            .multiple_results = false,
        });
    ApplyLuaValueOperation(
        context,
        LuaValueOperation{
            .reg = operation.method_reg,
            .expression = operation.method_expression,
            .multiple_results = false,
        });
}

void ApplyLuaGlobalSetOperation(const LuaOperationRenderContext& context,
                                const LuaGlobalSetOperation& operation) {
    if (context.lines == nullptr || context.values == nullptr) {
        return;
    }

    if (RewriteTrailingRenderedTableBuilderTarget(
            context.lines, operation.expression, operation.global_name)) {
        if (operation.source_reg >= 0 &&
            static_cast<std::size_t>(operation.source_reg) < context.values->size()) {
            (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                StraightLineValue{operation.global_name, true, false};
        }
        return;
    }

    ApplyLuaStatementOperation(
        context,
        LuaStatementOperation{
            .statement = RenderAssignmentStatement(operation.global_name, operation.expression),
        });
    if (operation.source_reg >= 0 &&
        static_cast<std::size_t>(operation.source_reg) < context.values->size()) {
        if (!operation.source_expression_override.empty()) {
            (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                StraightLineValue{operation.source_expression_override,
                                  true,
                                  operation.source_multiple_results_override};
        } else {
            (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                StraightLineValue{operation.global_name, true, false};
        }
    }
}

void ApplyLuaUpvalueSetOperation(const LuaOperationRenderContext& context,
                                 const LuaUpvalueSetOperation& operation) {
    ApplyLuaStatementOperation(
        context,
        LuaStatementOperation{
            .statement = RenderAssignmentStatement(operation.upvalue_name, operation.expression),
        });
    if (context.values != nullptr &&
        operation.source_reg >= 0 &&
        static_cast<std::size_t>(operation.source_reg) < context.values->size()) {
        if (!operation.source_expression_override.empty()) {
            (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                StraightLineValue{operation.source_expression_override,
                                  true,
                                  operation.source_multiple_results_override};
        } else {
            (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                StraightLineValue{operation.upvalue_name, true, false};
        }
    }
}

void ApplyLuaTableSetOperation(const LuaOperationRenderContext& context,
                               const LuaTableSetOperation& operation) {
    if (context.values == nullptr || context.declared == nullptr) {
        return;
    }

    std::string materialized_table_name;
    const std::string statement =
        BuildLuaTableSetStatement(operation.table_expression,
                                  operation.key_expression,
                                  operation.expression,
                                  operation.fallback_name,
                                  operation.declare_local,
                                  &materialized_table_name);

    if (context.lines != nullptr) {
        ApplyLuaStatementOperation(
            context,
            LuaStatementOperation{
                .statement = statement,
            });
    }

    if (!materialized_table_name.empty() &&
        operation.target_reg >= 0 &&
        static_cast<std::size_t>(operation.target_reg) < context.values->size()) {
        (*context.declared)[static_cast<std::size_t>(operation.target_reg)] = true;
        (*context.values)[static_cast<std::size_t>(operation.target_reg)] =
            StraightLineValue{materialized_table_name, true, false};
        return;
    }

    if (operation.target_reg >= 0 &&
        static_cast<std::size_t>(operation.target_reg) < context.values->size()) {
        const std::string_view trimmed_table =
            TrimLuaWhitespace(operation.table_expression);
        if (IsSimpleAssignableLuaExpression(trimmed_table)) {
            (*context.values)[static_cast<std::size_t>(operation.target_reg)] =
                StraightLineValue{std::string(trimmed_table), true, false};
        }
    }

    if (operation.source_reg >= 0 &&
        static_cast<std::size_t>(operation.source_reg) < context.values->size() &&
        !operation.source_expression_override.empty()) {
        (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
            StraightLineValue{operation.source_expression_override,
                              true,
                              operation.source_multiple_results_override};
    }
}

void ApplyLuaSetListOperation(const LuaOperationRenderContext& context,
                              const LuaSetListOperation& operation) {
    if (operation.try_inline_initializer &&
        TryInlineTrailingSetListInitializer(
            context.lines, operation.table_expression, operation.entries)) {
        return;
    }
    for (std::size_t offset = 0; offset < operation.entries.size(); ++offset) {
        const StraightLineValue& value = operation.entries[offset];
        ApplyLuaStatementOperation(
            context,
            LuaStatementOperation{
                .statement = RenderAssignmentStatement(
                    RenderLuaTableAccessExpression(
                        operation.table_expression,
                        std::to_string(operation.base_index + static_cast<int>(offset))),
                    value.text),
            });
    }
}

bool ApplyLuaCallOperation(const LuaOperationRenderContext& context,
                           const LuaCallOperation& operation) {
    if (operation.tail_return) {
        ApplyLuaReturnOperation(
            context,
            LuaReturnOperation{
                .expression = operation.expression,
                .has_values = true,
            });
        return true;
    }

    switch (operation.result_mode) {
        case LuaCallResultMode::kDiscard:
            ApplyLuaStatementOperation(
                context, LuaStatementOperation{.statement = operation.expression});
            if (context.values != nullptr &&
                operation.source_reg >= 0 &&
                static_cast<std::size_t>(operation.source_reg) < context.values->size() &&
                !operation.source_expression_override.empty()) {
                (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                    StraightLineValue{operation.source_expression_override,
                                      true,
                                      operation.source_multiple_results_override};
            }
            return false;
        case LuaCallResultMode::kStoreMultiple:
            ApplyLuaValueOperation(
                context,
                LuaValueOperation{
                    .reg = operation.target_reg,
                    .expression = operation.expression,
                    .multiple_results = true,
                });
            return false;
        case LuaCallResultMode::kStoreSingle:
            ApplyLuaAssignmentOperation(
                context,
                LuaAssignmentOperation{
                    .first_reg = operation.target_reg,
                    .count = 1,
                    .line = operation.line,
                    .expression = operation.expression,
                    .multiple_results = false,
                });
            return false;
        case LuaCallResultMode::kStoreFixed:
            ApplyLuaAssignmentOperation(
                context,
                LuaAssignmentOperation{
                    .first_reg = operation.target_reg,
                    .count = operation.fixed_result_count,
                    .line = operation.line,
                    .expression = operation.expression,
                    .multiple_results = false,
                });
            return false;
    }

    return false;
}

void ApplyLuaReturnOperation(const LuaOperationRenderContext& context,
                             const LuaReturnOperation& operation) {
    if (operation.has_values) {
        ApplyLuaStatementOperation(
            context,
            LuaStatementOperation{
                .statement = RenderReturnStatement(operation.expression, true),
            });
        return;
    }
    ApplyLuaStatementOperation(
        context, LuaStatementOperation{.statement = RenderReturnStatement({}, false)});
}

bool IsLuaBuiltOperationSideEffectFree(const LuaBuiltOperation& operation) {
    return std::visit(
        [&](const auto& payload) -> bool {
            using T = std::decay_t<decltype(payload)>;
            if constexpr (std::is_same_v<T, std::monostate>) {
                return true;
            } else if constexpr (std::is_same_v<T, LuaAssignmentOperation> ||
                                 std::is_same_v<T, LuaValueOperation> ||
                                 std::is_same_v<T, LuaSelfOperation>) {
                return !operation.stop_render;
            } else {
                return false;
            }
        },
        operation.payload);
}

bool TryRenderLuaBuiltOperationStatement(const LuaOperationRenderContext& context,
                                         const LuaBuiltOperation& operation,
                                         std::string* statement) {
    if (statement == nullptr) {
        return false;
    }

    return std::visit(
        [&](const auto& payload) -> bool {
            using T = std::decay_t<decltype(payload)>;
            if constexpr (std::is_same_v<T, std::monostate>) {
                return false;
            } else if constexpr (std::is_same_v<T, LuaStatementOperation>) {
                *statement = payload.statement;
                return true;
            } else if constexpr (std::is_same_v<T, LuaGlobalSetOperation>) {
                *statement = RenderAssignmentStatement(payload.global_name, payload.expression);
                return true;
            } else if constexpr (std::is_same_v<T, LuaUpvalueSetOperation>) {
                *statement = RenderAssignmentStatement(payload.upvalue_name, payload.expression);
                return true;
            } else if constexpr (std::is_same_v<T, LuaTableSetOperation>) {
                *statement = BuildLuaTableSetStatement(payload.table_expression,
                                                      payload.key_expression,
                                                      payload.expression,
                                                      payload.fallback_name,
                                                      payload.declare_local,
                                                      nullptr);
                return true;
            } else if constexpr (std::is_same_v<T, LuaCallOperation>) {
                if (payload.tail_return) {
                    *statement = RenderReturnStatement(payload.expression, true);
                    return true;
                }
                if (payload.result_mode == LuaCallResultMode::kDiscard) {
                    *statement = payload.expression;
                    return true;
                }
                return false;
            } else if constexpr (std::is_same_v<T, LuaReturnOperation>) {
                *statement = RenderReturnStatement(payload.expression, payload.has_values);
                return true;
            } else {
                return false;
            }
        },
        operation.payload);
}

bool ApplyLuaBuiltOperation(const LuaOperationRenderContext& context,
                            const LuaBuiltOperation& operation) {
    const bool applied_stop = std::visit(
        [&](const auto& payload) -> bool {
            using T = std::decay_t<decltype(payload)>;
            if constexpr (std::is_same_v<T, std::monostate>) {
                return false;
            } else if constexpr (std::is_same_v<T, LuaAssignmentOperation>) {
                ApplyLuaAssignmentOperation(context, payload);
                return false;
            } else if constexpr (std::is_same_v<T, LuaValueOperation>) {
                ApplyLuaValueOperation(context, payload);
                return false;
            } else if constexpr (std::is_same_v<T, LuaStatementOperation>) {
                ApplyLuaStatementOperation(context, payload);
                return false;
            } else if constexpr (std::is_same_v<T, LuaSelfOperation>) {
                ApplyLuaSelfOperation(context, payload);
                return false;
            } else if constexpr (std::is_same_v<T, LuaGlobalSetOperation>) {
                ApplyLuaGlobalSetOperation(context, payload);
                return false;
            } else if constexpr (std::is_same_v<T, LuaUpvalueSetOperation>) {
                ApplyLuaUpvalueSetOperation(context, payload);
                return false;
            } else if constexpr (std::is_same_v<T, LuaTableSetOperation>) {
                ApplyLuaTableSetOperation(context, payload);
                return false;
            } else if constexpr (std::is_same_v<T, LuaSetListOperation>) {
                ApplyLuaSetListOperation(context, payload);
                return false;
            } else if constexpr (std::is_same_v<T, LuaCallOperation>) {
                return ApplyLuaCallOperation(context, payload);
            } else if constexpr (std::is_same_v<T, LuaReturnOperation>) {
                ApplyLuaReturnOperation(context, payload);
                return true;
            }
            return false;
        },
        operation.payload);
    if (context.values != nullptr) {
        for (const LuaValueOperation& post_apply_value : operation.post_apply_values) {
            ApplyLuaValueOperation(context, post_apply_value);
        }
    }
    return operation.stop_render || applied_stop;
}

}  // namespace refii::lua
