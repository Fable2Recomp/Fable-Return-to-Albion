#include "tools/decompiler/operation_build.h"

#include "tools/decompiler/preview_operation_render.h"
#include "tools/decompiler/rendered_lua_text.h"

#include <sstream>

namespace refii::lua {
namespace {

int ComputeLuaClosureNextCurrent(const LuaOperationBuildContext& context,
                                 int current,
                                 int child_index,
                                 std::string* error) {
    if (child_index < 0 ||
        static_cast<std::size_t>(child_index) >= context.function.child_functions.size()) {
        if (error != nullptr) {
            *error = "invalid_closure_child";
        }
        return -1;
    }

    const Lua51Function& child_function =
        context.function.child_functions[static_cast<std::size_t>(child_index)];
    int next_current = current + 1;
    for (int offset = 0; offset < static_cast<int>(child_function.upvalue_count);
         ++offset, ++next_current) {
        if (next_current < 1 ||
            static_cast<std::size_t>(next_current) > context.instructions.size()) {
            if (error != nullptr) {
                *error = "truncated_closure_bindings";
            }
            return -1;
        }

        const Lua51Instruction& binding =
            context.instructions[static_cast<std::size_t>(next_current - 1)];
        if (binding.opcode != Lua51Opcode::kMove &&
            binding.opcode != Lua51Opcode::kGetUpval) {
            if (error != nullptr) {
                *error = "unsupported_closure_binding@pc=" +
                         std::to_string(next_current) + ",opcode=" +
                         std::string(DescribeLua51Opcode(binding.opcode));
            }
            return -1;
        }
    }

    return next_current;
}

int ComputeLuaAssignmentNextCurrent(const LuaOperationBuildContext& context,
                                    int current,
                                    std::string* error) {
    if (current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
        if (error != nullptr) {
            *error = "invalid_assignment_pc";
        }
        return -1;
    }

    const Lua51Instruction& instruction =
        context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode == Lua51Opcode::kLoadBool &&
        instruction.c != 0 && IsLoadBoolPair(context.instructions, current)) {
        return current + 2;
    }
    if (instruction.opcode == Lua51Opcode::kClosure) {
        return ComputeLuaClosureNextCurrent(context, current, instruction.bx, error);
    }
    return current + 1;
}

void ComputeLuaCallResultMode(const Lua51Instruction& instruction,
                              LuaCallResultMode* result_mode,
                              int* fixed_result_count) {
    if (result_mode == nullptr || fixed_result_count == nullptr) {
        return;
    }

    *result_mode = LuaCallResultMode::kDiscard;
    *fixed_result_count = 0;
    if (instruction.opcode == Lua51Opcode::kTailCall) {
        return;
    }

    if (instruction.c == 0) {
        *result_mode = LuaCallResultMode::kStoreMultiple;
    } else if (instruction.c == 2) {
        *result_mode = LuaCallResultMode::kStoreSingle;
    } else if (instruction.c > 2) {
        *result_mode = LuaCallResultMode::kStoreFixed;
        *fixed_result_count = instruction.c - 1;
    }
}

bool TryPreviewLuaShortcutOperation(const LuaOperationBuildContext& context,
                                    int current,
                                    int line,
                                    std::vector<StraightLineValue>* preview_values,
                                    std::vector<bool>* preview_declared,
                                    int* next_current) {
    if (preview_values == nullptr || preview_declared == nullptr || next_current == nullptr) {
        return false;
    }
    if (current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
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
    if (!TryApplyLuaPreviewOperation(preview_context,
                                     current,
                                     line,
                                     LuaPreviewOperationMode::kSideEffectFreeOnly,
                                     &disposition,
                                     &build_error,
                                     next_current)) {
        return false;
    }
    return true;
}

bool TryBuildLuaClosureDirectOperation(const LuaOperationBuildContext& context,
                                       int current,
                                       int line,
                                       std::string* error,
                                       LuaBuiltOperation* operation) {
    if (operation == nullptr || error == nullptr || context.render_child == nullptr ||
        current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& instruction =
        context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kClosure) {
        return false;
    }

    std::string rendered_child;
    if (!context.render_child(instruction.bx, &rendered_child)) {
        return false;
    }

    std::string next_error;
    const int closure_next_current =
        ComputeLuaClosureNextCurrent(context, current, instruction.bx, &next_error);
    if (closure_next_current < 0) {
        *error = std::move(next_error);
        return false;
    }
    if (static_cast<std::size_t>(closure_next_current) > context.instructions.size()) {
        return false;
    }

    int effective_current = closure_next_current;
    int effective_register = instruction.a;
    std::vector<LuaValueOperation> preserved_closure_values;
    while (static_cast<std::size_t>(effective_current) <= context.instructions.size()) {
        const Lua51Instruction& maybe_move =
            context.instructions[static_cast<std::size_t>(effective_current - 1)];
        if (maybe_move.opcode != Lua51Opcode::kMove || maybe_move.b != effective_register) {
            break;
        }
        preserved_closure_values.push_back(
            LuaValueOperation{
                .reg = effective_register,
                .expression = rendered_child,
                .multiple_results = false,
            });
        effective_register = maybe_move.a;
        ++effective_current;
    }
    if (static_cast<std::size_t>(effective_current) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& next =
        context.instructions[static_cast<std::size_t>(effective_current - 1)];
    if (next.opcode == Lua51Opcode::kSetGlobal && next.a == effective_register) {
        if (next.bx < 0 ||
            static_cast<std::size_t>(next.bx) >= context.function.constants.size() ||
            context.function.constants[static_cast<std::size_t>(next.bx)].type !=
                Lua51ConstantType::kString) {
            *error = "invalid_setglobal_name";
            return false;
        }
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = false,
            .post_apply_values = std::move(preserved_closure_values),
            .payload =
                LuaGlobalSetOperation{
                    .global_name = RenderGlobalExpression(
                        context.function.constants[static_cast<std::size_t>(next.bx)].string_value),
                    .expression = rendered_child,
                    .source_reg = effective_register,
                    .source_expression_override = rendered_child,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    if (next.opcode == Lua51Opcode::kSetUpval && next.a == effective_register) {
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = false,
            .post_apply_values = std::move(preserved_closure_values),
            .payload =
                LuaUpvalueSetOperation{
                    .upvalue_name = RenderUpvalueExpression(context.function, next.b),
                    .expression = rendered_child,
                    .source_reg = effective_register,
                    .source_expression_override = rendered_child,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    const int next_line = static_cast<int>(next.line == 0 ? effective_current : next.line);
    if (next.opcode == Lua51Opcode::kSetTable &&
        context.values != nullptr) {
        const StraightLineValue table =
            GetRegisterValue(*context.values,
                             context.declarations,
                             context.function,
                             next.a,
                             next_line);
        const StraightLineValue key =
            GetRKValue(*context.values,
                       context.declarations,
                       context.function,
                       next.b,
                       next_line);
        const bool declare_local =
            next.a >= context.function.parameter_count &&
            context.declarations.Get(next.a, next_line) == nullptr &&
            (context.declared == nullptr ||
             (next.a >= 0 &&
              static_cast<std::size_t>(next.a) < context.declared->size() &&
              !(*context.declared)[static_cast<std::size_t>(next.a)]));
        if (next.c == effective_register) {
            *operation = LuaBuiltOperation{
                .next_current = effective_current + 1,
                .stop_render = false,
                .post_apply_values = std::move(preserved_closure_values),
                .payload =
                    LuaTableSetOperation{
                        .table_expression = table.text,
                        .key_expression = key.text,
                        .expression = rendered_child,
                        .fallback_name = GetStraightLineRegisterName(
                            context.declarations, context.function, next.a, next_line),
                        .declare_local = declare_local,
                        .target_reg = next.a,
                        .source_reg = effective_register,
                        .source_expression_override = rendered_child,
                        .source_multiple_results_override = false,
                    },
            };
            return true;
        }
    }

    if ((next.opcode == Lua51Opcode::kCall || next.opcode == Lua51Opcode::kTailCall) &&
        next.a == effective_register && context.values != nullptr && context.call_arguments_for) {
        const std::vector<StraightLineValue> arguments =
            context.call_arguments_for(*context.values, next.a, next.b, next_line);
        LuaCallResultMode result_mode = LuaCallResultMode::kDiscard;
        int fixed_result_count = 0;
        ComputeLuaCallResultMode(next, &result_mode, &fixed_result_count);
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = next.opcode == Lua51Opcode::kTailCall,
            .post_apply_values = std::move(preserved_closure_values),
            .payload =
                LuaCallOperation{
                    .target_reg = next.a,
                    .fixed_result_count = fixed_result_count,
                    .line = next_line,
                    .expression = RenderLuaCallExpression(rendered_child, arguments),
                    .result_mode = result_mode,
                    .tail_return = next.opcode == Lua51Opcode::kTailCall,
                    .source_reg = effective_register,
                    .source_expression_override = rendered_child,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    if (next.opcode == Lua51Opcode::kReturn && next.a == effective_register && next.b == 2) {
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = true,
            .payload =
                LuaReturnOperation{
                    .expression = rendered_child,
                    .has_values = true,
                },
        };
        return true;
    }

    return false;
}

bool TryBuildLuaDirectTableLiteralOperation(const LuaOperationBuildContext& context,
                                            int current,
                                            int line,
                                            std::string* error,
                                            LuaBuiltOperation* operation) {
    if (operation == nullptr || context.values == nullptr ||
        context.declared == nullptr || current < 1 ||
        static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& instruction =
        context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kNewTable) {
        return false;
    }

    std::vector<StraightLineValue> preview_values = *context.values;
    std::vector<bool> preview_declared = *context.declared;
    std::string literal_expression = RenderTableLiteralExpression({});
    int effective_current = current + 1;
    bool consumed_setlist = false;

    while (static_cast<std::size_t>(effective_current) <= context.instructions.size()) {
        const Lua51Instruction& candidate =
            context.instructions[static_cast<std::size_t>(effective_current - 1)];
        if (candidate.opcode == Lua51Opcode::kSetList && candidate.a == instruction.a) {
            if (!context.resolve_setlist_block || !context.setlist_values_for) {
                return false;
            }
            int extra_words = 0;
            const int block =
                context.resolve_setlist_block(effective_current,
                                              candidate,
                                              &extra_words,
                                              nullptr);
            if (block < 0) {
                return false;
            }
            const std::vector<StraightLineValue> entries =
                context.setlist_values_for(preview_values,
                                           candidate.a,
                                           candidate.b,
                                           static_cast<int>(candidate.line == 0
                                                                ? effective_current
                                                                : candidate.line));
            if (entries.empty()) {
                return false;
            }
            literal_expression = RenderTableLiteralExpression(entries);
            effective_current += extra_words + 1;
            consumed_setlist = true;
            break;
        }

        if (candidate.opcode == Lua51Opcode::kMove ||
            candidate.opcode == Lua51Opcode::kSetGlobal ||
            candidate.opcode == Lua51Opcode::kSetUpval ||
            candidate.opcode == Lua51Opcode::kSetTable ||
            candidate.opcode == Lua51Opcode::kReturn) {
            break;
        }

        int next_current = effective_current + 1;
        if (!TryPreviewLuaShortcutOperation(context,
                                            effective_current,
                                            static_cast<int>(candidate.line == 0
                                                                 ? effective_current
                                                                 : candidate.line),
                                            &preview_values,
                                            &preview_declared,
                                            &next_current)) {
            return false;
        }
        effective_current = next_current;
    }

    if (static_cast<std::size_t>(effective_current) > context.instructions.size()) {
        return false;
    }

    int effective_register = instruction.a;
    std::vector<LuaValueOperation> preserved_table_values;
    while (static_cast<std::size_t>(effective_current) <= context.instructions.size()) {
        const Lua51Instruction& maybe_move =
            context.instructions[static_cast<std::size_t>(effective_current - 1)];
        if (maybe_move.opcode != Lua51Opcode::kMove || maybe_move.b != effective_register) {
            break;
        }
        preserved_table_values.push_back(
            LuaValueOperation{
                .reg = effective_register,
                .expression = literal_expression,
                .multiple_results = false,
            });
        effective_register = maybe_move.a;
        ++effective_current;
    }

    if (static_cast<std::size_t>(effective_current) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& next =
        context.instructions[static_cast<std::size_t>(effective_current - 1)];
    const int next_line = static_cast<int>(next.line == 0 ? effective_current : next.line);

    if (next.opcode == Lua51Opcode::kSetGlobal && next.a == effective_register) {
        if (next.bx < 0 ||
            static_cast<std::size_t>(next.bx) >= context.function.constants.size() ||
            context.function.constants[static_cast<std::size_t>(next.bx)].type !=
                Lua51ConstantType::kString) {
            return false;
        }
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = false,
            .post_apply_values = std::move(preserved_table_values),
            .payload =
                LuaGlobalSetOperation{
                    .global_name = RenderGlobalExpression(
                        context.function.constants[static_cast<std::size_t>(next.bx)].string_value),
                    .expression = literal_expression,
                    .source_reg = effective_register,
                    .source_expression_override = literal_expression,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    if (next.opcode == Lua51Opcode::kSetUpval && next.a == effective_register) {
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = false,
            .post_apply_values = std::move(preserved_table_values),
            .payload =
                LuaUpvalueSetOperation{
                    .upvalue_name = RenderUpvalueExpression(context.function, next.b),
                    .expression = literal_expression,
                    .source_reg = effective_register,
                    .source_expression_override = literal_expression,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    if (next.opcode == Lua51Opcode::kSetTable && next.c == effective_register) {
        const StraightLineValue table =
            GetRegisterValue(preview_values,
                             context.declarations,
                             context.function,
                             next.a,
                             next_line);
        const StraightLineValue key =
            GetRKValue(preview_values,
                       context.declarations,
                       context.function,
                       next.b,
                       next_line);
        const bool declare_local =
            next.a >= context.function.parameter_count &&
            context.declarations.Get(next.a, next_line) == nullptr &&
            (context.declared == nullptr ||
             (next.a >= 0 &&
              static_cast<std::size_t>(next.a) < context.declared->size() &&
              !(*context.declared)[static_cast<std::size_t>(next.a)]));
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = false,
            .post_apply_values = std::move(preserved_table_values),
            .payload =
                LuaTableSetOperation{
                    .table_expression = table.text,
                    .key_expression = key.text,
                    .expression = literal_expression,
                    .fallback_name = GetStraightLineRegisterName(
                        context.declarations, context.function, next.a, next_line),
                    .declare_local = declare_local,
                    .target_reg = next.a,
                    .source_reg = effective_register,
                    .source_expression_override = literal_expression,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    if (next.opcode == Lua51Opcode::kReturn && next.a == effective_register && next.b == 2) {
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = true,
            .payload =
                LuaReturnOperation{
                    .expression = literal_expression,
                    .has_values = true,
                },
        };
        return true;
    }

    (void)consumed_setlist;
    return false;
}

bool TryBuildLuaDirectSafeAssignmentOperation(const LuaOperationBuildContext& context,
                                              int current,
                                              int line,
                                              std::string* error,
                                              LuaBuiltOperation* operation) {
    if (operation == nullptr || context.values == nullptr || current < 1 ||
        static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
    }

    LuaAssignmentOperation assignment;
    std::string build_error;
    const Lua51Instruction& instruction =
        context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode == Lua51Opcode::kMove) {
        return false;
    }
    if (!TryBuildLuaAssignmentOperation(context, instruction, line, &build_error, &assignment)) {
        if (error != nullptr && !build_error.empty()) {
            *error = std::move(build_error);
        }
        return false;
    }

    if (assignment.count != 1 || assignment.multiple_results ||
        !IsSafeStraightLinePropagation(assignment.expression)) {
        return false;
    }

    const int assignment_next_current =
        ComputeLuaAssignmentNextCurrent(context, current, &build_error);
    if (assignment_next_current < 0) {
        if (error != nullptr && !build_error.empty()) {
            *error = std::move(build_error);
        }
        return false;
    }
    if (static_cast<std::size_t>(assignment_next_current) > context.instructions.size()) {
        return false;
    }

    int effective_current = assignment_next_current;
    int effective_register = assignment.first_reg;
    std::vector<LuaValueOperation> preserved_assignment_values;
    while (static_cast<std::size_t>(effective_current) <= context.instructions.size()) {
        const Lua51Instruction& maybe_move =
            context.instructions[static_cast<std::size_t>(effective_current - 1)];
        if (maybe_move.opcode != Lua51Opcode::kMove || maybe_move.b != effective_register) {
            break;
        }
        preserved_assignment_values.push_back(
            LuaValueOperation{
                .reg = effective_register,
                .expression = assignment.expression,
                .multiple_results = false,
            });
        effective_register = maybe_move.a;
        ++effective_current;
    }

    if (static_cast<std::size_t>(effective_current) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& next =
        context.instructions[static_cast<std::size_t>(effective_current - 1)];
    const int next_line = static_cast<int>(next.line == 0 ? effective_current : next.line);
    const std::string_view trimmed_assignment =
        TrimLuaWhitespace(assignment.expression);

    if (next.opcode == Lua51Opcode::kSetGlobal && next.a == effective_register) {
        if (next.bx < 0 ||
            static_cast<std::size_t>(next.bx) >= context.function.constants.size() ||
            context.function.constants[static_cast<std::size_t>(next.bx)].type !=
                Lua51ConstantType::kString) {
            if (error != nullptr) {
                *error = "invalid_setglobal_name";
            }
            return false;
        }
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = false,
            .post_apply_values = std::move(preserved_assignment_values),
            .payload =
                LuaGlobalSetOperation{
                    .global_name = RenderGlobalExpression(
                        context.function.constants[static_cast<std::size_t>(next.bx)].string_value),
                    .expression = assignment.expression,
                    .source_reg = effective_register,
                    .source_expression_override = assignment.expression,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    if (next.opcode == Lua51Opcode::kSetUpval && next.a == effective_register) {
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = false,
            .post_apply_values = std::move(preserved_assignment_values),
            .payload =
                LuaUpvalueSetOperation{
                    .upvalue_name = RenderUpvalueExpression(context.function, next.b),
                    .expression = assignment.expression,
                    .source_reg = effective_register,
                    .source_expression_override = assignment.expression,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    if (next.opcode == Lua51Opcode::kSetTable && next.c == effective_register &&
        trimmed_assignment != "nil" &&
        trimmed_assignment != "false" &&
        trimmed_assignment != "true") {
        const StraightLineValue table =
            GetRegisterValue(*context.values,
                             context.declarations,
                             context.function,
                             next.a,
                             next_line);
        const StraightLineValue key =
            GetRKValue(*context.values,
                       context.declarations,
                       context.function,
                       next.b,
                       next_line);
        const bool declare_local =
            next.a >= context.function.parameter_count &&
            context.declarations.Get(next.a, next_line) == nullptr &&
            (context.declared == nullptr ||
             (next.a >= 0 &&
              static_cast<std::size_t>(next.a) < context.declared->size() &&
              !(*context.declared)[static_cast<std::size_t>(next.a)]));
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = false,
            .post_apply_values = std::move(preserved_assignment_values),
            .payload =
                LuaTableSetOperation{
                    .table_expression = table.text,
                    .key_expression = key.text,
                    .expression = assignment.expression,
                    .fallback_name = GetStraightLineRegisterName(
                        context.declarations, context.function, next.a, next_line),
                    .declare_local = declare_local,
                    .target_reg = next.a,
                    .source_reg = effective_register,
                    .source_expression_override = assignment.expression,
                    .source_multiple_results_override = false,
                },
        };
        return true;
    }

    if (next.opcode == Lua51Opcode::kReturn && next.a == effective_register && next.b == 2) {
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = true,
            .payload =
                LuaReturnOperation{
                    .expression = assignment.expression,
                    .has_values = true,
                },
        };
        return true;
    }

    return false;
}

}  // namespace

bool TryBuildLuaNewTableShortcutOperation(const LuaOperationBuildContext& context,
                                          int current,
                                          int line,
                                          LuaAssignmentOperation* assignment,
                                          int* next_current) {
    if (assignment == nullptr || next_current == nullptr ||
        static_cast<std::size_t>(current) >= context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& instruction = context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kNewTable ||
        current + 2 > static_cast<int>(context.instructions.size())) {
        return false;
    }

    const Lua51Instruction& next = context.instructions[static_cast<std::size_t>(current)];
    const Lua51Instruction& next_next =
        context.instructions[static_cast<std::size_t>(current + 1)];
    if (next.opcode == Lua51Opcode::kVarArg && next.a == instruction.a + 1 &&
        next.b == 0 && next_next.opcode == Lua51Opcode::kSetList &&
        next_next.a == instruction.a && next_next.b == 0) {
        const std::vector<StraightLineValue> entries{StraightLineValue{"...", true, true}};
        *assignment = LuaAssignmentOperation{
            .first_reg = instruction.a,
            .count = 1,
            .line = line,
            .expression = RenderTableLiteralExpression(entries),
            .multiple_results = false,
        };
        *next_current = current + 3;
        return true;
    }

    if (current + 3 > static_cast<int>(context.instructions.size()) ||
        context.values == nullptr || context.declared == nullptr ||
        !context.call_arguments_for) {
        return false;
    }

    std::vector<StraightLineValue> preview_values = *context.values;
    std::vector<bool> preview_declared = *context.declared;
    int maybe_call_current = current + 1;

    if (next.opcode != Lua51Opcode::kCall) {
        if (!TryPreviewLuaShortcutOperation(context,
                                            current + 1,
                                            current + 1,
                                            &preview_values,
                                            &preview_declared,
                                            &maybe_call_current)) {
            return false;
        }
    }

    if (maybe_call_current < 1 ||
        static_cast<std::size_t>(maybe_call_current + 1) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& maybe_call =
        context.instructions[static_cast<std::size_t>(maybe_call_current - 1)];
    const Lua51Instruction& maybe_setlist =
        context.instructions[static_cast<std::size_t>(maybe_call_current)];
    if (maybe_call.opcode != Lua51Opcode::kCall || maybe_call.a != instruction.a + 1 ||
        maybe_call.c != 0 || maybe_setlist.opcode != Lua51Opcode::kSetList ||
        maybe_setlist.a != instruction.a || maybe_setlist.b != 0) {
        return false;
    }

    const StraightLineValue callee =
        GetRegisterValue(preview_values,
                         context.declarations,
                         context.function,
                         maybe_call.a,
                         maybe_call_current);
    const std::vector<StraightLineValue> arguments =
        context.call_arguments_for(preview_values,
                                   maybe_call.a,
                                   maybe_call.b,
                                   maybe_call_current);
    const std::vector<StraightLineValue> entries{StraightLineValue{
        RenderCallExpression(callee, arguments), true, false}};
    *assignment = LuaAssignmentOperation{
        .first_reg = instruction.a,
        .count = 1,
        .line = line,
        .expression = RenderTableLiteralExpression(entries),
        .multiple_results = false,
    };
    *next_current = maybe_call_current + 2;
    return true;
}

bool TryBuildLuaAssignmentOperation(const LuaOperationBuildContext& context,
                                    const Lua51Instruction& instruction,
                                    int line,
                                    std::string* error,
                                    LuaAssignmentOperation* assignment) {
    if (assignment == nullptr || context.values == nullptr) {
        return false;
    }

    switch (instruction.opcode) {
        case Lua51Opcode::kMove: {
            const StraightLineValue rhs =
                GetRegisterValue(*context.values, context.declarations, context.function, instruction.b, line);
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = rhs.text,
                .multiple_results = rhs.multiple_results,
            };
            return true;
        }
        case Lua51Opcode::kLoadK:
            if (instruction.bx < 0 ||
                static_cast<std::size_t>(instruction.bx) >= context.function.constants.size()) {
                if (error != nullptr) {
                    *error = "invalid_constant_index";
                }
                return false;
            }
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderConstantExpression(
                    context.function.constants[static_cast<std::size_t>(instruction.bx)]),
                .multiple_results = false,
            };
            return true;
        case Lua51Opcode::kLoadBool:
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderBooleanLiteralExpression(instruction.b != 0),
                .multiple_results = false,
            };
            return true;
        case Lua51Opcode::kLoadNil:
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = std::max(0, instruction.b - instruction.a + 1),
                .line = line,
                .expression = RenderNilExpression(),
                .multiple_results = false,
            };
            return assignment->count > 0;
        case Lua51Opcode::kGetUpval:
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderUpvalueExpression(context.function, instruction.b),
                .multiple_results = false,
            };
            return true;
        case Lua51Opcode::kGetGlobal:
            if (instruction.bx < 0 ||
                static_cast<std::size_t>(instruction.bx) >= context.function.constants.size() ||
                context.function.constants[static_cast<std::size_t>(instruction.bx)].type !=
                    Lua51ConstantType::kString) {
                if (error != nullptr) {
                    *error = "invalid_global_name";
                }
                return false;
            }
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderGlobalExpression(
                    context.function.constants[static_cast<std::size_t>(instruction.bx)].string_value),
                .multiple_results = false,
            };
            return true;
        case Lua51Opcode::kGetTable: {
            const StraightLineValue table =
                GetRegisterValue(*context.values, context.declarations, context.function, instruction.b, line);
            const StraightLineValue key =
                GetRKValue(*context.values, context.declarations, context.function, instruction.c, line);
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderTableAccessValueExpression(table, key),
                .multiple_results = false,
            };
            return true;
        }
        case Lua51Opcode::kNewTable:
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderTableLiteralExpression({}),
                .multiple_results = false,
            };
            return true;
        case Lua51Opcode::kAdd:
        case Lua51Opcode::kSub:
        case Lua51Opcode::kMul:
        case Lua51Opcode::kDiv:
        case Lua51Opcode::kMod:
        case Lua51Opcode::kPow: {
            const StraightLineValue lhs =
                GetRKValue(*context.values, context.declarations, context.function, instruction.b, line);
            const StraightLineValue rhs =
                GetRKValue(*context.values, context.declarations, context.function, instruction.c, line);
            std::string_view op = "+";
            switch (instruction.opcode) {
                case Lua51Opcode::kAdd: op = "+"; break;
                case Lua51Opcode::kSub: op = "-"; break;
                case Lua51Opcode::kMul: op = "*"; break;
                case Lua51Opcode::kDiv: op = "/"; break;
                case Lua51Opcode::kMod: op = "%"; break;
                case Lua51Opcode::kPow: op = "^"; break;
                default: break;
            }
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderBinaryExpression(lhs.text, op, rhs.text),
                .multiple_results = false,
            };
            return true;
        }
        case Lua51Opcode::kUnm: {
            const StraightLineValue rhs =
                GetRegisterValue(*context.values, context.declarations, context.function, instruction.b, line);
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderUnaryExpression("-", rhs.text),
                .multiple_results = false,
            };
            return true;
        }
        case Lua51Opcode::kNot: {
            const StraightLineValue rhs =
                GetRegisterValue(*context.values, context.declarations, context.function, instruction.b, line);
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderUnaryExpression("not", rhs.text),
                .multiple_results = false,
            };
            return true;
        }
        case Lua51Opcode::kLen: {
            const StraightLineValue rhs =
                GetRegisterValue(*context.values, context.declarations, context.function, instruction.b, line);
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderUnaryExpression("#", rhs.text),
                .multiple_results = false,
            };
            return true;
        }
        case Lua51Opcode::kConcat: {
            std::vector<StraightLineValue> values;
            for (int reg = instruction.b; reg <= instruction.c && reg < context.register_count; ++reg) {
                values.push_back(
                    GetRegisterValue(*context.values, context.declarations, context.function, reg, line));
            }
            *assignment = LuaAssignmentOperation{
                .first_reg = instruction.a,
                .count = 1,
                .line = line,
                .expression = RenderConcatenationExpression(values),
                .multiple_results = false,
            };
            return true;
        }
        case Lua51Opcode::kClosure: {
            if (context.render_child) {
                std::string rendered_child;
                if (context.render_child(instruction.bx, &rendered_child)) {
                    *assignment = LuaAssignmentOperation{
                        .first_reg = instruction.a,
                        .count = 1,
                        .line = line,
                        .expression = rendered_child,
                        .multiple_results = false,
                    };
                    return true;
                }
            }
            if (context.allow_opaque_preview_closures) {
                *assignment = LuaAssignmentOperation{
                    .first_reg = instruction.a,
                    .count = 1,
                    .line = line,
                    .expression = GetStraightLineRegisterName(
                        context.declarations, context.function, instruction.a, line),
                    .multiple_results = false,
                };
                return true;
            }
            if (error != nullptr && error->empty()) {
                *error = "unsupported_nested_closure";
            }
            return false;
        }
        case Lua51Opcode::kVarArg:
            if (instruction.b == 2) {
                *assignment = LuaAssignmentOperation{
                    .first_reg = instruction.a,
                    .count = 1,
                    .line = line,
                    .expression = RenderVarargExpression(),
                    .multiple_results = false,
                };
                return true;
            }
            if (instruction.b > 2) {
                *assignment = LuaAssignmentOperation{
                    .first_reg = instruction.a,
                    .count = instruction.b - 1,
                    .line = line,
                    .expression = RenderVarargExpression(),
                    .multiple_results = false,
                };
                return true;
            }
            return false;
        default:
            return false;
    }
}

bool TryBuildLuaValueOperation(const LuaOperationBuildContext& context,
                               const Lua51Instruction& instruction,
                               int,
                               LuaValueOperation* value_operation) {
    if (value_operation == nullptr) {
        return false;
    }
    if (instruction.opcode == Lua51Opcode::kVarArg && instruction.b == 0) {
        *value_operation = LuaValueOperation{
            .reg = instruction.a,
            .expression = RenderVarargExpression(),
            .multiple_results = true,
        };
        return true;
    }
    return false;
}

bool TryBuildLuaSelfOperation(const LuaOperationBuildContext& context,
                              const Lua51Instruction& instruction,
                              int line,
                              LuaSelfOperation* self_operation) {
    if (self_operation == nullptr || context.values == nullptr ||
        instruction.opcode != Lua51Opcode::kSelf) {
        return false;
    }
    const StraightLineValue table =
        GetRegisterValue(*context.values, context.declarations, context.function, instruction.b, line);
    const StraightLineValue key =
        GetRKValue(*context.values, context.declarations, context.function, instruction.c, line);
    *self_operation = LuaSelfOperation{
        .method_reg = instruction.a,
        .object_reg = instruction.a + 1,
        .method_expression = RenderTableAccessValueExpression(table, key),
        .object_expression = table.text,
    };
    return true;
}

bool TryBuildLuaCallOperation(const LuaOperationBuildContext& context,
                              int current,
                              int line,
                              LuaCallOperation* call_operation) {
    if (call_operation == nullptr || context.values == nullptr || !context.call_arguments_for ||
        static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
    }
    const Lua51Instruction& instruction = context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kCall &&
        instruction.opcode != Lua51Opcode::kTailCall) {
        return false;
    }
    const StraightLineValue callee =
        GetRegisterValue(*context.values, context.declarations, context.function, instruction.a, line);
    const std::vector<StraightLineValue> arguments =
        context.call_arguments_for(*context.values, instruction.a, instruction.b, line);
    LuaCallResultMode result_mode = LuaCallResultMode::kDiscard;
    int fixed_result_count = 0;
    ComputeLuaCallResultMode(instruction, &result_mode, &fixed_result_count);
    *call_operation = LuaCallOperation{
        .target_reg = instruction.a,
        .fixed_result_count = fixed_result_count,
        .line = line,
        .expression = RenderCallExpression(callee, arguments),
        .result_mode = result_mode,
        .tail_return = instruction.opcode == Lua51Opcode::kTailCall,
    };
    if (context.trace_render && context.trace) {
        std::ostringstream stream;
        stream << "call pc=" << current
               << " a=" << instruction.a
               << " b=" << instruction.b
               << " c=" << instruction.c
               << " callee=" << callee.text
               << " expr=" << call_operation->expression;
        context.trace(stream.str());
    }
    return true;
}

bool TryBuildLuaReturnOperation(const LuaOperationBuildContext& context,
                                int start_line,
                                int end_line,
                                int current,
                                int line,
                                LuaReturnOperation* return_operation,
                                bool* suppress_return) {
    if (return_operation == nullptr || suppress_return == nullptr ||
        !context.return_values_for || context.values == nullptr ||
        static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
    }
    const Lua51Instruction& instruction = context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kReturn) {
        return false;
    }
    *suppress_return = false;
    if (current > start_line) {
        const Lua51Instruction& previous_instruction =
            context.instructions[static_cast<std::size_t>(current - 2)];
        if (previous_instruction.opcode == Lua51Opcode::kReturn ||
            previous_instruction.opcode == Lua51Opcode::kTailCall) {
            *suppress_return = true;
            return true;
        }
    }
    const std::vector<StraightLineValue> results =
        context.return_values_for(*context.values, instruction.a, instruction.b, line);
    if (results.empty() && current == end_line) {
        *suppress_return = true;
        return true;
    }
    *return_operation = LuaReturnOperation{
        .expression = RenderExpressionList(results),
        .has_values = !results.empty(),
    };
    return true;
}

bool TryBuildLuaGlobalSetOperation(const LuaOperationBuildContext& context,
                                   const Lua51Instruction& instruction,
                                   int line,
                                   std::string* error,
                                   LuaGlobalSetOperation* operation) {
    if (operation == nullptr || context.values == nullptr ||
        instruction.opcode != Lua51Opcode::kSetGlobal) {
        return false;
    }
    if (instruction.bx < 0 ||
        static_cast<std::size_t>(instruction.bx) >= context.function.constants.size() ||
        context.function.constants[static_cast<std::size_t>(instruction.bx)].type !=
            Lua51ConstantType::kString) {
        if (error != nullptr) {
            *error = "invalid_setglobal_name";
        }
        return false;
    }
    const StraightLineValue rhs =
        GetRegisterValue(*context.values, context.declarations, context.function, instruction.a, line);
    *operation = LuaGlobalSetOperation{
        .global_name = RenderGlobalExpression(
            context.function.constants[static_cast<std::size_t>(instruction.bx)].string_value),
        .expression = rhs.text,
        .source_reg = instruction.a,
    };
    return true;
}

bool TryBuildLuaUpvalueSetOperation(const LuaOperationBuildContext& context,
                                    const Lua51Instruction& instruction,
                                    int line,
                                    LuaUpvalueSetOperation* operation) {
    if (operation == nullptr || context.values == nullptr ||
        instruction.opcode != Lua51Opcode::kSetUpval) {
        return false;
    }
    const StraightLineValue rhs =
        GetRegisterValue(*context.values, context.declarations, context.function, instruction.a, line);
    *operation = LuaUpvalueSetOperation{
        .upvalue_name = RenderUpvalueExpression(context.function, instruction.b),
        .expression = rhs.text,
    };
    return true;
}

bool TryBuildLuaTableSetOperation(const LuaOperationBuildContext& context,
                                  const Lua51Instruction& instruction,
                                  int line,
                                  LuaTableSetOperation* operation) {
    if (operation == nullptr || context.values == nullptr ||
        instruction.opcode != Lua51Opcode::kSetTable) {
        return false;
    }
    const StraightLineValue table =
        GetRegisterValue(*context.values, context.declarations, context.function, instruction.a, line);
    const StraightLineValue key =
        GetRKValue(*context.values, context.declarations, context.function, instruction.b, line);
    const StraightLineValue rhs =
        GetRKValue(*context.values, context.declarations, context.function, instruction.c, line);
    const bool declare_local =
        instruction.a >= context.function.parameter_count &&
        context.declarations.Get(instruction.a, line) == nullptr &&
        (context.declared == nullptr ||
         (instruction.a >= 0 &&
          static_cast<std::size_t>(instruction.a) < context.declared->size() &&
          !(*context.declared)[static_cast<std::size_t>(instruction.a)]));
    *operation = LuaTableSetOperation{
        .table_expression = table.text,
        .key_expression = key.text,
        .expression = rhs.text,
        .fallback_name =
            GetStraightLineRegisterName(context.declarations, context.function, instruction.a, line),
        .declare_local = declare_local,
        .target_reg = instruction.a,
    };
    return true;
}

bool TryBuildLuaSetListOperation(const LuaOperationBuildContext& context,
                                 int current,
                                 int line,
                                 std::string* error,
                                 LuaSetListOperation* operation,
                                 int* next_current,
                                 bool* emit_whole_table_assignment,
                                 std::string* whole_table_assignment) {
    if (operation == nullptr || next_current == nullptr ||
        emit_whole_table_assignment == nullptr || whole_table_assignment == nullptr ||
        context.values == nullptr || !context.resolve_setlist_block || !context.setlist_values_for ||
        static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
    }
    const Lua51Instruction& instruction = context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kSetList) {
        return false;
    }

    const StraightLineValue table =
        GetRegisterValue(*context.values, context.declarations, context.function, instruction.a, line);
    int extra_words = 0;
    const int block = context.resolve_setlist_block(current, instruction, &extra_words, error);
    if (block < 0) {
        return false;
    }

    const std::vector<StraightLineValue> entries =
        context.setlist_values_for(*context.values, instruction.a, instruction.b, line);
    if (entries.empty()) {
        if (error != nullptr) {
            *error = "unsupported_dynamic_setlist";
        }
        return false;
    }

    const int base = (std::max(1, block) - 1) * 50 + 1;
    *next_current = current + extra_words + 1;
    *emit_whole_table_assignment = false;
    whole_table_assignment->clear();

    if (instruction.b == 0 && entries.size() == 1 && entries.front().multiple_results && base == 1) {
        *emit_whole_table_assignment = true;
        *whole_table_assignment =
            RenderAssignmentStatement(table.text, RenderTableLiteralExpression(entries));
        return true;
    }

    *operation = LuaSetListOperation{
        .table_expression = table.text,
        .base_index = base,
        .entries = entries,
        .try_inline_initializer = true,
    };
    return true;
}

bool TryBuildLuaBuiltOperation(const LuaOperationBuildContext& context,
                               int start_line,
                               int end_line,
                               int current,
                               int line,
                               std::string* error,
                               LuaBuiltOperation* operation) {
    if (operation == nullptr) {
        return false;
    }

    std::string build_error;
    LuaAssignmentOperation assignment;
    int next_current = -1;
    if (TryBuildLuaNewTableShortcutOperation(
            context, current, line, &assignment, &next_current)) {
        *operation = LuaBuiltOperation{
            .next_current = next_current,
            .stop_render = false,
            .payload = std::move(assignment),
        };
        return true;
    }

    if (TryBuildLuaDirectTableLiteralOperation(
            context, current, line, error == nullptr ? &build_error : error, operation)) {
        return true;
    }
    if (error == nullptr ? !build_error.empty() : !error->empty()) {
        return false;
    }

    if (TryBuildLuaClosureDirectOperation(
            context, current, line, error == nullptr ? &build_error : error, operation)) {
        return true;
    }
    if (error == nullptr ? !build_error.empty() : !error->empty()) {
        return false;
    }

    if (TryBuildLuaDirectSafeAssignmentOperation(
            context, current, line, error == nullptr ? &build_error : error, operation)) {
        return true;
    }
    if (error == nullptr ? !build_error.empty() : !error->empty()) {
        return false;
    }

    if (TryBuildLuaAssignmentOperation(
            context,
            context.instructions[static_cast<std::size_t>(current - 1)],
            line,
            &build_error,
            &assignment)) {
        const int built_next_current =
            ComputeLuaAssignmentNextCurrent(context, current, &build_error);
        if (built_next_current < 0) {
            if (error != nullptr) {
                *error = std::move(build_error);
            }
            return false;
        }
        *operation = LuaBuiltOperation{
            .next_current = built_next_current,
            .stop_render = false,
            .payload = std::move(assignment),
        };
        return true;
    }
    if (!build_error.empty()) {
        if (error != nullptr) {
            *error = std::move(build_error);
        }
        return false;
    }

    LuaValueOperation value_operation;
    if (TryBuildLuaValueOperation(
            context,
            context.instructions[static_cast<std::size_t>(current - 1)],
            line,
            &value_operation)) {
        *operation = LuaBuiltOperation{
            .next_current = current + 1,
            .stop_render = false,
            .payload = std::move(value_operation),
        };
        return true;
    }

    LuaSelfOperation self_operation;
    if (TryBuildLuaSelfOperation(
            context,
            context.instructions[static_cast<std::size_t>(current - 1)],
            line,
            &self_operation)) {
        *operation = LuaBuiltOperation{
            .next_current = current + 1,
            .stop_render = false,
            .payload = std::move(self_operation),
        };
        return true;
    }

    LuaCallOperation call_operation;
    if (TryBuildLuaCallOperation(context, current, line, &call_operation)) {
        *operation = LuaBuiltOperation{
            .next_current = current + 1,
            .stop_render = call_operation.tail_return,
            .payload = std::move(call_operation),
        };
        return true;
    }

    LuaReturnOperation return_operation;
    bool suppress_return = false;
    if (TryBuildLuaReturnOperation(
            context,
            start_line,
            end_line,
            current,
            line,
            &return_operation,
            &suppress_return)) {
        *operation = LuaBuiltOperation{
            .next_current = current + 1,
            .stop_render = true,
            .payload = suppress_return ? LuaBuiltOperationPayload(std::monostate{})
                                       : LuaBuiltOperationPayload(std::move(return_operation)),
        };
        return true;
    }

    LuaGlobalSetOperation global_set;
    build_error.clear();
    if (TryBuildLuaGlobalSetOperation(
            context,
            context.instructions[static_cast<std::size_t>(current - 1)],
            line,
            &build_error,
            &global_set)) {
        *operation = LuaBuiltOperation{
            .next_current = current + 1,
            .stop_render = false,
            .payload = std::move(global_set),
        };
        return true;
    }
    if (!build_error.empty()) {
        if (error != nullptr) {
            *error = std::move(build_error);
        }
        return false;
    }

    LuaUpvalueSetOperation upvalue_set;
    if (TryBuildLuaUpvalueSetOperation(
            context,
            context.instructions[static_cast<std::size_t>(current - 1)],
            line,
            &upvalue_set)) {
        *operation = LuaBuiltOperation{
            .next_current = current + 1,
            .stop_render = false,
            .payload = std::move(upvalue_set),
        };
        return true;
    }

    LuaTableSetOperation table_set;
    if (TryBuildLuaTableSetOperation(
            context,
            context.instructions[static_cast<std::size_t>(current - 1)],
            line,
            &table_set)) {
        *operation = LuaBuiltOperation{
            .next_current = current + 1,
            .stop_render = false,
            .payload = std::move(table_set),
        };
        return true;
    }

    LuaSetListOperation setlist_operation;
    bool emit_whole_table_assignment = false;
    std::string whole_table_assignment;
    build_error.clear();
    if (TryBuildLuaSetListOperation(context,
                                    current,
                                    line,
                                    &build_error,
                                    &setlist_operation,
                                    &next_current,
                                    &emit_whole_table_assignment,
                                    &whole_table_assignment)) {
        if (emit_whole_table_assignment) {
            *operation = LuaBuiltOperation{
                .next_current = next_current,
                .stop_render = false,
                .payload = LuaStatementOperation{.statement = std::move(whole_table_assignment)},
            };
        } else {
            *operation = LuaBuiltOperation{
                .next_current = next_current,
                .stop_render = false,
                .payload = std::move(setlist_operation),
            };
        }
        return true;
    }
    if (!build_error.empty()) {
        if (error != nullptr) {
            *error = std::move(build_error);
        }
        return false;
    }

    return false;
}

}  // namespace refii::lua
