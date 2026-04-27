#include "tools/decompiler/operation_render.h"

#include "tools/decompiler/rendered_lua_text.h"

namespace refii::lua {
namespace {

bool IsPreviewTableLiteralExpression(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    return trimmed.size() >= 2 && trimmed.front() == '{' && trimmed.back() == '}';
}

bool IsSafePreviewTableLiteralInitializerExpression(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return false;
    }
    return IsSafeLuaJoinPreservation(trimmed) ||
           (trimmed.front() == '{' && trimmed.back() == '}');
}

std::string BuildPreviewTableLiteralField(std::string_view key_expression,
                                         std::string_view value_expression) {
    const std::string_view trimmed_key = TrimLuaWhitespace(key_expression);
    const std::string_view trimmed_value = TrimLuaWhitespace(value_expression);
    if (trimmed_key.size() >= 2 &&
        ((trimmed_key.front() == '"' && trimmed_key.back() == '"') ||
         (trimmed_key.front() == '\'' && trimmed_key.back() == '\''))) {
        const std::string_view inner = trimmed_key.substr(1, trimmed_key.size() - 2);
        if (IsValidLuaIdentifier(inner)) {
            return std::string(inner) + " = " + std::string(trimmed_value);
        }
    }
    return "[" + std::string(trimmed_key) + "] = " + std::string(trimmed_value);
}

bool TryAppendPreviewTableLiteralField(std::string* table_literal,
                                       std::string_view table_expression,
                                       std::string_view key_expression,
                                       std::string_view value_expression) {
    if (table_literal == nullptr || !IsPreviewTableLiteralExpression(*table_literal) ||
        !IsSafePreviewTableLiteralInitializerExpression(key_expression) ||
        !IsSafePreviewTableLiteralInitializerExpression(value_expression)) {
        return false;
    }

    const std::string_view trimmed_table = TrimLuaWhitespace(table_expression);
    const std::string_view trimmed_key = TrimLuaWhitespace(key_expression);
    const std::string_view trimmed_value = TrimLuaWhitespace(value_expression);
    if ((!trimmed_table.empty() &&
         (CountIdentifierUses(trimmed_key, trimmed_table) != 0 ||
          CountIdentifierUses(trimmed_value, trimmed_table) != 0)) ||
        trimmed_key.empty() || trimmed_value.empty()) {
        return false;
    }

    const std::string field = BuildPreviewTableLiteralField(trimmed_key, trimmed_value);
    const std::string_view trimmed_literal = TrimLuaWhitespace(*table_literal);
    if (trimmed_literal == "{}") {
        *table_literal = "{" + field + "}";
        return true;
    }

    std::string merged;
    merged.reserve(trimmed_literal.size() + field.size() + 2);
    merged.append(trimmed_literal.substr(0, trimmed_literal.size() - 1));
    merged.append(", ");
    merged.append(field);
    merged.push_back('}');
    *table_literal = std::move(merged);
    return true;
}

bool HasRenderedTableSeedBeforeLine(const std::vector<std::string>& lines,
                                    std::size_t line_limit,
                                    std::string_view table_expression) {
    const std::string normalized_target = std::string(TrimLuaWhitespace(table_expression));
    if (normalized_target.empty()) {
        return false;
    }

    const std::size_t safe_limit = std::min(line_limit, lines.size());
    std::size_t scan_start = 0;
    const std::string_view trimmed_target = TrimLuaWhitespace(table_expression);
    std::string_view root_target = trimmed_target;
    const std::size_t dot = root_target.find('.');
    const std::size_t bracket = root_target.find('[');
    std::size_t split = std::string_view::npos;
    if (dot != std::string_view::npos) {
        split = dot;
    }
    if (bracket != std::string_view::npos &&
        (split == std::string_view::npos || bracket < split)) {
        split = bracket;
    }
    if (split != std::string_view::npos) {
        root_target = TrimLuaWhitespace(root_target.substr(0, split));
    }

    if (!root_target.empty() && root_target != normalized_target &&
        IsValidLuaIdentifier(root_target)) {
        std::vector<std::string> lhs_expressions;
        std::string rhs_expression;
        for (std::size_t probe = safe_limit; probe-- > 0;) {
            bool is_local = false;
            lhs_expressions.clear();
            rhs_expression.clear();
            if (ParseRenderedAssignmentLine(lines[probe],
                                            &is_local,
                                            &lhs_expressions,
                                            &rhs_expression)) {
                for (const std::string& lhs_expression : lhs_expressions) {
                    if (TrimLuaWhitespace(lhs_expression) == root_target) {
                        scan_start = std::min(safe_limit, probe + 1);
                        probe = 0;
                        break;
                    }
                }
                if (scan_start != 0) {
                    break;
                }
            }

            std::string indent;
            std::string lhs_prefix;
            std::string parsed_target;
            if (ParseRenderedTableLiteralStartLine(lines[probe],
                                                   &indent,
                                                   &lhs_prefix,
                                                   &parsed_target) &&
                TrimLuaWhitespace(parsed_target) == root_target) {
                scan_start = std::min(safe_limit, probe + 1);
                break;
            }
        }
    }

    for (std::size_t probe = scan_start; probe < safe_limit; ++probe) {
        bool is_local = false;
        std::vector<std::string> lhs_expressions;
        std::string rhs_expression;
        if (ParseRenderedAssignmentLine(lines[probe],
                                        &is_local,
                                        &lhs_expressions,
                                        &rhs_expression)) {
            for (const std::string& lhs_expression : lhs_expressions) {
                if (TrimLuaWhitespace(lhs_expression) == normalized_target) {
                    return true;
                }
            }
        }

        std::string indent;
        std::string lhs_prefix;
        std::string parsed_target;
        if (ParseRenderedTableLiteralStartLine(lines[probe],
                                               &indent,
                                               &lhs_prefix,
                                               &parsed_target)) {
            if (TrimLuaWhitespace(parsed_target) == normalized_target) {
                return true;
            }
        }
    }

    return false;
}

}

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
    if (context.values == nullptr) {
        return;
    }

    if (context.lines != nullptr) {
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
    }
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
    if (context.values == nullptr) {
        return;
    }

    if (context.lines != nullptr) {
        if (RewriteTrailingRenderedTableBuilderTarget(
                context.lines, operation.expression, operation.upvalue_name)) {
            if (operation.source_reg >= 0 &&
                static_cast<std::size_t>(operation.source_reg) < context.values->size()) {
                (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                    StraightLineValue{operation.upvalue_name, true, false};
            }
            return;
        }
    }

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

    std::string table_expression = operation.table_expression;
    if (context.lines != nullptr) {
        const std::string_view trimmed_table = TrimLuaWhitespace(table_expression);
        const bool has_member_chain =
            trimmed_table.find('.') != std::string_view::npos ||
            trimmed_table.find('[') != std::string_view::npos;
        if (has_member_chain &&
            IsSimpleAssignableLuaExpression(trimmed_table) &&
            !HasRenderedTableSeedBeforeLine(*context.lines,
                                            context.lines->size(),
                                            trimmed_table)) {
            // Materialize missing member-builder seeds eagerly so subsequent
            // field writes do not target an uninitialized member table.
            ApplyLuaStatementOperation(
                context,
                LuaStatementOperation{
                    .statement = RenderAssignmentStatement(trimmed_table, "{}"),
                });
            if (operation.target_reg >= 0 &&
                static_cast<std::size_t>(operation.target_reg) < context.values->size()) {
                (*context.values)[static_cast<std::size_t>(operation.target_reg)] =
                    StraightLineValue{std::string(trimmed_table), true, false};
            }
        }
    }

    const std::string target_expression =
        RenderLuaTableAccessExpression(table_expression, operation.key_expression);
    const std::string_view trimmed_table_expression =
        TrimLuaWhitespace(table_expression);
    const bool table_base_assignable =
        IsSimpleAssignableLuaExpression(trimmed_table_expression);

    if (context.lines != nullptr) {
        if (table_base_assignable &&
            RewriteTrailingRenderedTableBuilderTarget(
                context.lines, operation.expression, target_expression)) {
            if (operation.source_reg >= 0 &&
                static_cast<std::size_t>(operation.source_reg) < context.values->size()) {
                const std::string_view trimmed_target =
                    TrimLuaWhitespace(target_expression);
                const bool target_has_member_chain =
                    trimmed_target.find('.') != std::string_view::npos ||
                    trimmed_target.find('[') != std::string_view::npos;
                if (!operation.source_expression_override.empty()) {
                    (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                        StraightLineValue{operation.source_expression_override,
                                          true,
                                          operation.source_multiple_results_override};
                } else if (!target_has_member_chain &&
                           IsSimpleAssignableLuaExpression(trimmed_target)) {
                    (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                        StraightLineValue{target_expression, true, false};
                }
            }
            if (operation.target_reg >= 0 &&
                static_cast<std::size_t>(operation.target_reg) < context.values->size()) {
                const std::string_view trimmed_table =
                    TrimLuaWhitespace(table_expression);
                if (IsSimpleAssignableLuaExpression(trimmed_table)) {
                    (*context.values)[static_cast<std::size_t>(operation.target_reg)] =
                        StraightLineValue{std::string(trimmed_table), true, false};
                }
            }
            return;
        }
    } else if (operation.target_reg >= 0 &&
               static_cast<std::size_t>(operation.target_reg) < context.values->size()) {
        StraightLineValue& target_value =
            (*context.values)[static_cast<std::size_t>(operation.target_reg)];
        if (TryAppendPreviewTableLiteralField(&target_value.text,
                                             table_expression,
                                             operation.key_expression,
                                             operation.expression)) {
            target_value.valid = true;
            target_value.multiple_results = false;
            if (operation.source_reg >= 0 &&
                static_cast<std::size_t>(operation.source_reg) < context.values->size() &&
                !operation.source_expression_override.empty()) {
                (*context.values)[static_cast<std::size_t>(operation.source_reg)] =
                    StraightLineValue{operation.source_expression_override,
                                      true,
                                      operation.source_multiple_results_override};
            }
            return;
        }
    }

    std::string materialized_table_name;
    const std::string statement =
        BuildLuaTableSetStatement(table_expression,
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
        if (table_base_assignable) {
            (*context.values)[static_cast<std::size_t>(operation.target_reg)] =
                StraightLineValue{std::string(trimmed_table_expression), true, false};
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
    const auto try_apply_literal_state = [&]() {
        if (context.values == nullptr || operation.target_reg < 0 ||
            static_cast<std::size_t>(operation.target_reg) >= context.values->size()) {
            return false;
        }
        StraightLineValue updated_value =
            (*context.values)[static_cast<std::size_t>(operation.target_reg)];
        bool appended_all = !operation.entries.empty();
        for (std::size_t offset = 0; offset < operation.entries.size(); ++offset) {
            if (!TryAppendPreviewTableLiteralField(
                    &updated_value.text,
                    operation.table_expression,
                    std::to_string(operation.base_index + static_cast<int>(offset)),
                    operation.entries[offset].text)) {
                appended_all = false;
                break;
            }
        }
        if (!appended_all) {
            return false;
        }
        updated_value.valid = true;
        updated_value.multiple_results = false;
        (*context.values)[static_cast<std::size_t>(operation.target_reg)] =
            std::move(updated_value);
        return true;
    };

    if (operation.try_inline_initializer &&
        TryInlineTrailingSetListInitializer(
            context.lines, operation.table_expression, operation.entries)) {
        try_apply_literal_state();
        return;
    }
    if (context.lines == nullptr && try_apply_literal_state()) {
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
    if (context.lines == nullptr &&
        context.values != nullptr &&
        operation.target_reg >= 0 &&
        static_cast<std::size_t>(operation.target_reg) < context.values->size()) {
        (*context.values)[static_cast<std::size_t>(operation.target_reg)] =
            StraightLineValue{std::string(TrimLuaWhitespace(operation.table_expression)),
                              true,
                              false};
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
