#include "tools/decompiler/operation_build.h"

#include "tools/decompiler/control_flow_graph.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/preview_operation_render.h"
#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>
#include <cctype>
#include <sstream>
#include <string>
#include <unordered_map>

namespace refii::lua {
namespace {

thread_local std::size_t g_lua_native_table_constructor_commits = 0;
thread_local std::size_t g_lua_native_setlist_constructor_links = 0;
thread_local std::size_t g_lua_native_table_constructor_fallbacks = 0;

bool IsLuaEnhancedTopLevelConstructorScope(const Lua51Function& function,
                                           bool* is_combatstyles_scope,
                                           bool* use_high_budget_scope) {
    static thread_local const Lua51Function* last_function = nullptr;
    static thread_local bool last_is_scope = false;
    static thread_local bool last_is_combatstyles_scope = false;
    static thread_local bool last_use_high_budget_scope = false;
    if (&function == last_function) {
        if (is_combatstyles_scope != nullptr) {
            *is_combatstyles_scope = last_is_combatstyles_scope;
        }
        if (use_high_budget_scope != nullptr) {
            *use_high_budget_scope = last_use_high_budget_scope;
        }
        return last_is_scope;
    }

    const LuaScriptPolicy policy = BuildLuaScriptPolicy(function);
    last_is_combatstyles_scope = policy.constructor_combatstyles_scope;
    last_use_high_budget_scope = policy.constructor_high_budget_scope;
    last_is_scope = policy.constructor_scope_enabled;
    last_function = &function;
    if (is_combatstyles_scope != nullptr) {
        *is_combatstyles_scope = last_is_combatstyles_scope;
    }
    if (use_high_budget_scope != nullptr) {
        *use_high_budget_scope = last_use_high_budget_scope;
    }
    return last_is_scope;
}

bool IsLuaConstructorDeepParityTarget(const Lua51Function& function) {
    static thread_local const Lua51Function* last_function = nullptr;
    static thread_local bool last_is_target = false;
    if (&function == last_function) {
        return last_is_target;
    }

    const LuaScriptProfile profile = ClassifyLuaScriptProfile(function);
    last_is_target = function.line_defined == 0 &&
                     profile.Has(LuaScriptProfileFlag::kConstructorDeepTarget);
    last_function = &function;
    return last_is_target;
}

bool IsLuaAICoreConstructorIdentityHotspot(const Lua51Function& function) {
    static thread_local const Lua51Function* last_function = nullptr;
    static thread_local bool last_is_hotspot = false;
    if (&function == last_function) {
        return last_is_hotspot;
    }

    const LuaScriptProfile profile = ClassifyLuaScriptProfile(function);
    last_is_hotspot = profile.Has(LuaScriptProfileFlag::kConstructorIdentityHotspot);
    last_function = &function;
    return last_is_hotspot;
}

bool IsLuaGroupMindAliasChainHotspot(const Lua51Function& function) {
    static thread_local const Lua51Function* last_function = nullptr;
    static thread_local bool last_is_hotspot = false;
    if (&function == last_function) {
        return last_is_hotspot;
    }

    const LuaScriptProfile profile = ClassifyLuaScriptProfile(function);
    last_is_hotspot = profile.Has(LuaScriptProfileFlag::kConstructorAliasHotspot);
    last_function = &function;
    return last_is_hotspot;
}

bool ShouldUseLuaCanonicalLoadBoolSkip(const Lua51Function& function) {
    (void)function;
    // LOADBOOL with C != 0 always skips the next instruction in Lua 5.1.
    return true;
}

bool LuaInstructionClobbersTrackedRegister(const Lua51Instruction& instruction,
                                          int tracked_register) {
    if (tracked_register < 0) {
        return false;
    }

    if (instruction.opcode == Lua51Opcode::kLoadNil) {
        return tracked_register >= instruction.a && tracked_register <= instruction.b;
    }
    if (instruction.opcode == Lua51Opcode::kSelf) {
        return tracked_register == instruction.a || tracked_register == instruction.a + 1;
    }
    if (instruction.opcode == Lua51Opcode::kForPrep ||
        instruction.opcode == Lua51Opcode::kForLoop) {
        return tracked_register >= instruction.a && tracked_register <= instruction.a + 3;
    }
    if (instruction.opcode == Lua51Opcode::kTForLoop) {
        if (instruction.c <= 0) {
            return tracked_register >= instruction.a + 2;
        }
        return tracked_register >= instruction.a + 3 &&
               tracked_register <= instruction.a + 2 + instruction.c;
    }
    if (instruction.opcode == Lua51Opcode::kCall ||
        instruction.opcode == Lua51Opcode::kTailCall ||
        instruction.opcode == Lua51Opcode::kVarArg) {
        if (instruction.c == 0) {
            return tracked_register >= instruction.a;
        }
        return tracked_register >= instruction.a &&
               tracked_register <= instruction.a + std::max(0, instruction.c - 2);
    }

    if (instruction.a != tracked_register) {
        return false;
    }

    switch (instruction.opcode) {
        case Lua51Opcode::kSetGlobal:
        case Lua51Opcode::kSetUpval:
        case Lua51Opcode::kSetTable:
        case Lua51Opcode::kSetList:
        case Lua51Opcode::kClose:
        case Lua51Opcode::kJmp:
        case Lua51Opcode::kEq:
        case Lua51Opcode::kLt:
        case Lua51Opcode::kLe:
        case Lua51Opcode::kTest:
            return false;
        default:
            return true;
    }
}

bool IsLuaOwnershipControlFlowBarrier(const Lua51Instruction& instruction) {
    if (instruction.opcode == Lua51Opcode::kJmp ||
        instruction.opcode == Lua51Opcode::kForPrep ||
        instruction.opcode == Lua51Opcode::kForLoop ||
        instruction.opcode == Lua51Opcode::kTForLoop) {
        return true;
    }
    return decompiler::IsConditionalBranchOpcode(instruction.opcode);
}

std::string ResolveLuaTableExpressionForSetTargetImpl(
    const LuaOperationBuildContext& context,
    const std::vector<StraightLineValue>& values,
    int table_register,
    int line,
    int search_before_current,
    int depth) {
    const StraightLineValue table =
        GetRegisterValue(values,
                         context.declarations,
                         context.function,
                         table_register,
                         line);
    const std::string normalized_table = std::string(TrimLuaWhitespace(table.text));
    if (!IsRenderTemporaryName(normalized_table) || search_before_current < 1 || depth > 8) {
        return table.text;
    }

    int tracked_register = table_register;
    const bool ai_core_constructor_hotspot =
        IsLuaAICoreConstructorIdentityHotspot(context.function);
    const bool groupmind_alias_hotspot =
        IsLuaGroupMindAliasChainHotspot(context.function);
    const bool strict_hotspot_non_r0 =
        ai_core_constructor_hotspot &&
        context.function.line_defined == 0 &&
        table_register > 0 &&
        table_register <= 6;
    const bool strict_groupmind_alias_chain =
        groupmind_alias_hotspot && context.function.line_defined == 0;
    int scan_budget = 128;
    if (context.function.line_defined == 0 && tracked_register == 0) {
        scan_budget = std::max(256, search_before_current);
        if (strict_groupmind_alias_chain) {
            scan_budget = std::max(scan_budget, 320);
        }
    } else if (ai_core_constructor_hotspot && tracked_register >= 0 &&
               tracked_register <= 6) {
        // Keep this bounded: wide enough for owner identity recovery on AI-core hotspots,
        // narrow enough to avoid full-range scans on every table mutation.
        scan_budget = std::max(160, std::min(search_before_current, 256));
    } else if (strict_groupmind_alias_chain) {
        scan_budget = std::max(192, std::min(search_before_current, 320));
    }
    const int scan_begin = std::max(1, search_before_current - scan_budget);
    constexpr int kStrictHotspotNonR0MaxProducerDistance = 96;
    constexpr int kRecentNewTableOwnershipWindow = 96;
    if (table_register > 0) {
        // Keep freshly materialized table registers stable. If a recent NEWTABLE writes the
        // same register in the current straight-line region, prefer the local register
        // identity over speculative member-chain recovery.
        const int recent_begin = std::max(1, search_before_current - kRecentNewTableOwnershipWindow);
        for (int probe = search_before_current; probe >= recent_begin; --probe) {
            const Lua51Instruction& candidate =
                context.instructions[static_cast<std::size_t>(probe - 1)];
            if (candidate.opcode == Lua51Opcode::kNewTable &&
                candidate.a == table_register) {
                return table.text;
            }
            if (IsLuaOwnershipControlFlowBarrier(candidate)) {
                break;
            }
            if (LuaInstructionClobbersTrackedRegister(candidate, table_register)) {
                break;
            }
        }

        // Conservative guard: if this register was most recently produced by NEWTABLE
        // in the local scan window, preserve its local identity. Promoting it to a
        // member chain too early can destabilize constructor parity on large scripts.
        for (int probe = search_before_current; probe >= scan_begin; --probe) {
            const Lua51Instruction& candidate =
                context.instructions[static_cast<std::size_t>(probe - 1)];
            if (!LuaInstructionClobbersTrackedRegister(candidate, table_register)) {
                continue;
            }
            if (candidate.opcode == Lua51Opcode::kNewTable) {
                return table.text;
            }
            break;
        }
    }

    int tracked_move_links = 0;
    std::vector<int> tracked_alias_registers{tracked_register};
    const auto is_tracked_alias_register = [&](int reg) {
        return reg >= 0 &&
               std::find(tracked_alias_registers.begin(),
                         tracked_alias_registers.end(),
                         reg) != tracked_alias_registers.end();
    };
    const auto add_tracked_alias_register = [&](int reg) {
        if (!is_tracked_alias_register(reg)) {
            tracked_alias_registers.push_back(reg);
        }
    };
    int tracked_member_links = 0;
    for (int cursor = search_before_current; cursor >= scan_begin; --cursor) {
        const int producer_distance = search_before_current - cursor;
        if (strict_hotspot_non_r0 &&
            producer_distance > kStrictHotspotNonR0MaxProducerDistance) {
            return table.text;
        }
        const Lua51Instruction& instruction =
            context.instructions[static_cast<std::size_t>(cursor - 1)];
        if (strict_hotspot_non_r0 && IsLuaOwnershipControlFlowBarrier(instruction)) {
            return table.text;
        }
        if (strict_groupmind_alias_chain &&
            IsLuaOwnershipControlFlowBarrier(instruction)) {
            return table.text;
        }
        if (strict_groupmind_alias_chain) {
            const bool touches_alias =
                is_tracked_alias_register(instruction.a) ||
                is_tracked_alias_register(instruction.b) ||
                is_tracked_alias_register(instruction.c);
            if (touches_alias) {
                const bool is_allowed_chain_step =
                    (instruction.opcode == Lua51Opcode::kMove &&
                     instruction.a == tracked_register && instruction.b >= 0) ||
                    (instruction.opcode == Lua51Opcode::kGetTable &&
                     instruction.a == tracked_register && instruction.b >= 0 &&
                     IsConstantReference(instruction.c)) ||
                    (instruction.opcode == Lua51Opcode::kGetGlobal &&
                     instruction.a == tracked_register) ||
                    (instruction.opcode == Lua51Opcode::kGetUpval &&
                     instruction.a == tracked_register);
                if (!is_allowed_chain_step) {
                    return table.text;
                }
            }
        }
        if (instruction.opcode == Lua51Opcode::kMove && instruction.a == tracked_register) {
            tracked_register = instruction.b;
            add_tracked_alias_register(tracked_register);
            ++tracked_move_links;
            if (strict_hotspot_non_r0 && tracked_move_links > 8) {
                return table.text;
            }
            continue;
        }
        if (instruction.opcode == Lua51Opcode::kGetTable &&
            instruction.a == tracked_register) {
            if (instruction.b < 0 || !IsConstantReference(instruction.c)) {
                return table.text;
            }
            const int key_index = instruction.c & 0xFF;
            if (key_index < 0 ||
                static_cast<std::size_t>(key_index) >= context.function.constants.size()) {
                return table.text;
            }
            const Lua51Constant& key_constant =
                context.function.constants[static_cast<std::size_t>(key_index)];
            if (strict_groupmind_alias_chain &&
                key_constant.type != Lua51ConstantType::kString) {
                return table.text;
            }
            ++tracked_member_links;
            if (strict_groupmind_alias_chain && tracked_member_links > 6) {
                return table.text;
            }
            const std::string base_expression = ResolveLuaTableExpressionForSetTargetImpl(
                context,
                values,
                instruction.b,
                line,
                cursor - 1,
                depth + 1);
            if (base_expression.empty()) {
                return table.text;
            }
            return RenderLuaTableAccessExpression(
                base_expression, RenderConstantExpression(key_constant));
        }
        if (instruction.opcode == Lua51Opcode::kGetGlobal && instruction.a == tracked_register) {
            if (instruction.bx < 0 ||
                static_cast<std::size_t>(instruction.bx) >= context.function.constants.size() ||
                context.function.constants[static_cast<std::size_t>(instruction.bx)].type !=
                    Lua51ConstantType::kString) {
                return table.text;
            }
            return RenderGlobalExpression(
                context.function.constants[static_cast<std::size_t>(instruction.bx)].string_value);
        }
        if (instruction.opcode == Lua51Opcode::kGetUpval && instruction.a == tracked_register) {
            return RenderUpvalueExpression(context.function, instruction.b);
        }
        if (LuaInstructionClobbersTrackedRegister(instruction, tracked_register)) {
            return table.text;
        }
    }

    return table.text;
}

std::string ResolveLuaTableExpressionForSetTarget(const LuaOperationBuildContext& context,
                                                  const std::vector<StraightLineValue>& values,
                                                  int table_register,
                                                  int line,
                                                  int search_before_current) {
    return ResolveLuaTableExpressionForSetTargetImpl(context,
                                                     values,
                                                     table_register,
                                                     line,
                                                     search_before_current,
                                                     0);
}

std::string BuildLuaOpaqueNestedClosureExpression(const Lua51Function& function,
                                                  int child_index) {
    (void)function;
    (void)child_index;
    return "function(...) end";
}

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
        instruction.c != 0) {
        const int instruction_count = static_cast<int>(context.instructions.size());
        return std::min(instruction_count + 1, current + 2);
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

bool TryParseRenderedPositiveInteger(std::string_view text, int* value) {
    const std::string_view trimmed = TrimLuaWhitespace(text);
    if (trimmed.empty()) {
        return false;
    }

    int parsed = 0;
    for (char ch : trimmed) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        if (std::isdigit(uch) == 0) {
            return false;
        }
        parsed = parsed * 10 + (ch - '0');
    }

    if (parsed <= 0) {
        return false;
    }

    if (value != nullptr) {
        *value = parsed;
    }
    return true;
}

bool IsSafeRenderedTableLiteralInitializerExpression(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return false;
    }

    if (IsSafeLuaJoinPreservation(trimmed)) {
        return true;
    }

    return trimmed.front() == '{' && trimmed.back() == '}';
}

std::string RenderLuaTableLiteralField(std::string_view key_expression,
                                       std::string_view value_expression,
                                       int expected_array_index,
                                       bool* consumed_array_slot) {
    const std::string_view trimmed_key = TrimLuaWhitespace(key_expression);
    const std::string_view trimmed_value = TrimLuaWhitespace(value_expression);

    int parsed_index = -1;
    if (expected_array_index > 0 &&
        TryParseRenderedPositiveInteger(trimmed_key, &parsed_index) &&
        parsed_index == expected_array_index) {
        if (consumed_array_slot != nullptr) {
            *consumed_array_slot = true;
        }
        return std::string(trimmed_value);
    }

    if (trimmed_key.size() >= 2 &&
        ((trimmed_key.front() == '"' && trimmed_key.back() == '"') ||
         (trimmed_key.front() == '\'' && trimmed_key.back() == '\''))) {
        const std::string_view inner = trimmed_key.substr(1, trimmed_key.size() - 2);
        if (IsValidLuaIdentifier(inner)) {
            if (consumed_array_slot != nullptr) {
                *consumed_array_slot = false;
            }
            return std::string(inner) + " = " + std::string(trimmed_value);
        }
    }

    if (consumed_array_slot != nullptr) {
        *consumed_array_slot = false;
    }
    return "[" + std::string(trimmed_key) + "] = " + std::string(trimmed_value);
}

std::string RenderLuaMixedTableLiteralExpression(const std::vector<std::string>& entries) {
    if (entries.empty()) {
        return "{}";
    }

    std::string expression = "{";
    for (std::size_t index = 0; index < entries.size(); ++index) {
        if (index != 0) {
            expression += ", ";
        }
        expression += entries[index];
    }
    expression += "}";
    return expression;
}

bool TryPreviewLuaShortcutOperation(const LuaOperationBuildContext& context,
                                    int current,
                                    int line,
                                    LuaPreviewOperationMode mode,
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
        .setlist_values_for = context.setlist_values_for,
        .render_child = context.render_child,
        .resolve_setlist_block = context.resolve_setlist_block,
    };
    LuaPreviewOperationDisposition disposition = LuaPreviewOperationDisposition::kBuildFailure;
    std::string build_error;
    if (!TryApplyLuaPreviewOperation(preview_context,
                                     current,
                                     line,
                                     mode,
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
        const std::string resolved_table_expression =
            ResolveLuaTableExpressionForSetTarget(context,
                                                  *context.values,
                                                  next.a,
                                                  next_line,
                                                  effective_current - 1);
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
                        .table_expression = resolved_table_expression,
                        .key_expression = key.text,
                        .expression = rendered_child,
                        .fallback_name = GetStraightLineAssignmentRegisterName(
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
                    .expression = RenderLuaCallExpression(rendered_child,
                                                          arguments,
                                                          context.declarations,
                                                          context.function,
                                                          next_line,
                                                          context.register_count),
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

bool TryBuildLuaDirectTableLiteralOperationLegacy(const LuaOperationBuildContext& context,
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
    std::vector<std::string> literal_entries;
    std::string literal_expression = RenderLuaMixedTableLiteralExpression(literal_entries);
    int expected_array_index = 1;
    int effective_current = current + 1;
    bool consumed_setlist = false;

    while (static_cast<std::size_t>(effective_current) <= context.instructions.size()) {
        const Lua51Instruction& candidate =
            context.instructions[static_cast<std::size_t>(effective_current - 1)];
        const int candidate_line = static_cast<int>(
            candidate.line == 0 ? effective_current : candidate.line);

        if (candidate.opcode == Lua51Opcode::kSetTable && candidate.a == instruction.a) {
            const StraightLineValue key =
                GetRKValue(preview_values,
                           context.declarations,
                           context.function,
                           candidate.b,
                           candidate_line);
            const StraightLineValue rhs =
                GetRKValue(preview_values,
                           context.declarations,
                           context.function,
                           candidate.c,
                           candidate_line);
            if (!key.valid || !rhs.valid ||
                !IsSafeLuaJoinPreservation(key.text) ||
                !IsSafeLuaJoinPreservation(rhs.text)) {
                break;
            }
            bool consumed_array_slot = false;
            literal_entries.push_back(RenderLuaTableLiteralField(
                key.text, rhs.text, expected_array_index, &consumed_array_slot));
            if (consumed_array_slot) {
                ++expected_array_index;
            }
            literal_expression = RenderLuaMixedTableLiteralExpression(literal_entries);
            ++effective_current;
            continue;
        }

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
                                           candidate_line);
            if (entries.empty()) {
                return false;
            }
            (void)block;
            for (const StraightLineValue& entry : entries) {
                literal_entries.push_back(entry.text);
                ++expected_array_index;
            }
            literal_expression = RenderLuaMixedTableLiteralExpression(literal_entries);
            consumed_setlist = true;
            effective_current += extra_words + 1;
            continue;
        }

        if ((candidate.opcode == Lua51Opcode::kMove && candidate.b == instruction.a) ||
            candidate.opcode == Lua51Opcode::kSetGlobal ||
            candidate.opcode == Lua51Opcode::kSetUpval ||
            (candidate.opcode == Lua51Opcode::kSetTable && candidate.c == instruction.a) ||
            candidate.opcode == Lua51Opcode::kReturn) {
            break;
        }

        int next_current = effective_current + 1;
        if (!TryPreviewLuaShortcutOperation(context,
                                            effective_current,
                                            static_cast<int>(candidate.line == 0
                                                                 ? effective_current
                                                                 : candidate.line),
                                            LuaPreviewOperationMode::kSideEffectFreeOnly,
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
        const std::string resolved_table_expression =
            ResolveLuaTableExpressionForSetTarget(context,
                                                  preview_values,
                                                  next.a,
                                                  next_line,
                                                  effective_current - 1);
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
                    .table_expression = resolved_table_expression,
                    .key_expression = key.text,
                    .expression = literal_expression,
                    .fallback_name = GetStraightLineAssignmentRegisterName(
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

bool TryBuildLuaDirectTableLiteralOperationOwnedConstructor(const LuaOperationBuildContext& context,
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

    constexpr std::size_t kMaxConstructorAliasCount = 8;
    constexpr std::size_t kMaxChildAliasSetSize = 4;
    constexpr int kPostSinkAliasMoveLimit = 8;
    constexpr int kPostSinkConstructorEntryLimit = 16;
    constexpr int kTopLevelConstructorPrecheckMaxSpan = 256;
    constexpr int kNestedConstructorPrecheckMaxSpan = 64;
    constexpr int kNestedConstructorSubSpanCap = 64;
    const bool top_level_constructor_scope = context.function.line_defined == 0;
    const bool deep_constructor_parity_target =
        top_level_constructor_scope &&
        IsLuaConstructorDeepParityTarget(context.function);
    const bool allow_segmented_local_constructor_sink = deep_constructor_parity_target;

    std::size_t scoped_setlist_links = 0;
    bool precheck_passed = false;
    bool precheck_segmented_local_sink = false;
    const auto reject_scoped_constructor = [&]() {
        if (precheck_passed) {
            ++g_lua_native_table_constructor_fallbacks;
        }
        return false;
    };
    const auto is_constructor_control_flow_barrier = [&](const Lua51Instruction& candidate) {
        if (candidate.opcode == Lua51Opcode::kJmp ||
            candidate.opcode == Lua51Opcode::kForLoop ||
            candidate.opcode == Lua51Opcode::kForPrep ||
            candidate.opcode == Lua51Opcode::kTForLoop) {
            return true;
        }
        return decompiler::IsConditionalBranchOpcode(candidate.opcode);
    };

    std::vector<int> precheck_aliases{instruction.a};
    std::vector<int> precheck_detached_aliases;
    const auto precheck_is_alias = [&](int reg) {
        return reg >= 0 &&
               std::find(precheck_aliases.begin(), precheck_aliases.end(), reg) !=
                   precheck_aliases.end();
    };
    const auto precheck_is_detached_alias = [&](int reg) {
        return reg >= 0 &&
               std::find(precheck_detached_aliases.begin(),
                         precheck_detached_aliases.end(),
                         reg) != precheck_detached_aliases.end();
    };
    const auto precheck_add_detached_alias = [&](int reg) {
        if (reg < 0 || precheck_is_detached_alias(reg) || precheck_is_alias(reg)) {
            return true;
        }
        if (precheck_detached_aliases.size() >= kMaxConstructorAliasCount) {
            return false;
        }
        precheck_detached_aliases.push_back(reg);
        return true;
    };
    const auto precheck_touches_alias = [&](const Lua51Instruction& candidate) {
        return precheck_is_alias(candidate.a) ||
               precheck_is_alias(candidate.b) ||
               precheck_is_alias(candidate.c);
    };
    const auto precheck_touches_detached_alias = [&](const Lua51Instruction& candidate) {
        return precheck_is_detached_alias(candidate.a) ||
               precheck_is_detached_alias(candidate.b) ||
               precheck_is_detached_alias(candidate.c);
    };
    const auto precheck_is_direct_sink = [&](const Lua51Instruction& candidate) {
        if (candidate.opcode == Lua51Opcode::kSetGlobal &&
            precheck_is_alias(candidate.a)) {
            return true;
        }
        if (candidate.opcode == Lua51Opcode::kSetUpval &&
            precheck_is_alias(candidate.a)) {
            return true;
        }
        if (candidate.opcode == Lua51Opcode::kReturn &&
            precheck_is_alias(candidate.a) &&
            candidate.b == 2) {
            return true;
        }
        return candidate.opcode == Lua51Opcode::kSetTable &&
               precheck_is_alias(candidate.c) &&
               !precheck_is_alias(candidate.a);
    };
    const int instruction_count = static_cast<int>(context.instructions.size());
    const int constructor_scan_end_default =
        top_level_constructor_scope
            ? instruction_count
            : std::min(instruction_count, current + kNestedConstructorSubSpanCap);
    const int constructor_scan_end = constructor_scan_end_default;
    const int constructor_precheck_max_span =
        top_level_constructor_scope ? kTopLevelConstructorPrecheckMaxSpan
                                    : kNestedConstructorPrecheckMaxSpan;
    const int precheck_end =
        std::min(constructor_scan_end, current + constructor_precheck_max_span);
    std::vector<StraightLineValue> precheck_preview_values = *context.values;
    std::vector<bool> precheck_preview_declared = *context.declared;
    bool saw_constructor_mutation = false;
    bool saw_plausible_sink = false;
    for (int probe = current + 1; probe <= precheck_end; ++probe) {
        const Lua51Instruction& candidate =
            context.instructions[static_cast<std::size_t>(probe - 1)];
        if (is_constructor_control_flow_barrier(candidate)) {
            return false;
        }
        if (candidate.opcode == Lua51Opcode::kMove && precheck_is_alias(candidate.b)) {
            if (!precheck_is_alias(candidate.a)) {
                if (precheck_aliases.size() >= kMaxConstructorAliasCount) {
                    return false;
                }
                precheck_aliases.push_back(candidate.a);
            }
            continue;
        }
        if (candidate.opcode == Lua51Opcode::kMove &&
            precheck_is_detached_alias(candidate.b)) {
            // Detached alias chains are too expensive and prone to partial constructor
            // commits on large scripts. Reject in precheck and fall back.
            return false;
        }
        if ((candidate.opcode == Lua51Opcode::kSetTable && precheck_is_alias(candidate.a)) ||
            (candidate.opcode == Lua51Opcode::kSetList && precheck_is_alias(candidate.a))) {
            saw_constructor_mutation = true;
            continue;
        }
        if (precheck_is_direct_sink(candidate)) {
            saw_plausible_sink = true;
            break;
        }
        if (precheck_touches_alias(candidate)) {
            return false;
        }
        if (candidate.opcode == Lua51Opcode::kNewTable &&
            (precheck_touches_alias(candidate) ||
             precheck_touches_detached_alias(candidate))) {
            // Constructor ownership cannot stay proven if a NEWTABLE clobbers an
            // owned alias in the precheck span.
            return false;
        }
        if ((candidate.opcode == Lua51Opcode::kSetTable ||
             candidate.opcode == Lua51Opcode::kSetList) &&
            precheck_is_detached_alias(candidate.a)) {
            // Detached table mutation before sink is an ambiguity for ownership.
            return false;
        }
        if (precheck_touches_detached_alias(candidate)) {
            return false;
        }
        int precheck_next_current = probe + 1;
        if (TryPreviewLuaShortcutOperation(context,
                                           probe,
                                           static_cast<int>(candidate.line == 0 ? probe
                                                                                 : candidate.line),
                                           LuaPreviewOperationMode::kSideEffectFreeOnly,
                                           &precheck_preview_values,
                                           &precheck_preview_declared,
                                           &precheck_next_current)) {
            if (precheck_next_current <= probe) {
                return false;
            }
            probe = precheck_next_current - 1;
            continue;
        }
        if (allow_segmented_local_constructor_sink && saw_constructor_mutation &&
            !precheck_touches_alias(candidate) &&
            !precheck_touches_detached_alias(candidate)) {
            precheck_segmented_local_sink = true;
            saw_plausible_sink = true;
            break;
        }
        break;
    }
    if (!saw_constructor_mutation || !saw_plausible_sink) {
        return false;
    }
    precheck_passed = true;

    std::vector<StraightLineValue> preview_values = *context.values;
    std::vector<bool> preview_declared = *context.declared;
    std::vector<int> table_alias_registers{instruction.a};
    std::vector<std::string> table_alias_names{GetStraightLineRegisterName(
        context.declarations, context.function, instruction.a, line)};
    std::vector<int> detached_table_alias_registers;
    const auto register_is_table_alias = [&](int reg) {
        return reg >= 0 &&
               std::find(table_alias_registers.begin(),
                         table_alias_registers.end(),
                         reg) != table_alias_registers.end();
    };
    const auto register_is_detached_table_alias = [&](int reg) {
        return reg >= 0 &&
               std::find(detached_table_alias_registers.begin(),
                         detached_table_alias_registers.end(),
                         reg) != detached_table_alias_registers.end();
    };
    const auto touches_table_alias_register = [&](const Lua51Instruction& candidate) {
        return register_is_table_alias(candidate.a) ||
               register_is_table_alias(candidate.b) ||
               register_is_table_alias(candidate.c);
    };
    const auto touches_detached_table_alias_register = [&](const Lua51Instruction& candidate) {
        return register_is_detached_table_alias(candidate.a) ||
               register_is_detached_table_alias(candidate.b) ||
               register_is_detached_table_alias(candidate.c);
    };
    const auto add_table_alias = [&](int reg, int alias_line) {
        if (reg < 0 || register_is_table_alias(reg)) {
            return true;
        }
        if (table_alias_registers.size() >= kMaxConstructorAliasCount) {
            return false;
        }
        table_alias_registers.push_back(reg);
        table_alias_names.push_back(GetStraightLineRegisterName(
            context.declarations, context.function, reg, alias_line));
        return true;
    };
    const auto add_detached_table_alias = [&](int reg) {
        if (reg < 0 || register_is_table_alias(reg) ||
            register_is_detached_table_alias(reg)) {
            return true;
        }
        if (detached_table_alias_registers.size() >= kMaxConstructorAliasCount) {
            return false;
        }
        detached_table_alias_registers.push_back(reg);
        return true;
    };
    std::vector<std::string> literal_entries;
    int expected_array_index = 1;
    int effective_current = current + 1;
    int effective_register = instruction.a;
    std::vector<int> preserved_owner_registers;
    int constructor_progress_stall_count = 0;
    const auto ensure_constructor_progress = [&](int from_current, int to_current) {
        if (to_current > from_current) {
            constructor_progress_stall_count = 0;
            return true;
        }
        ++constructor_progress_stall_count;
        return constructor_progress_stall_count < 2;
    };

    struct TrackedLiteralChildEntry {
        std::size_t entry_index = 0;
        int child_root_register = -1;
        std::string key_expression;
        int array_index = -1;
    };
    std::vector<TrackedLiteralChildEntry> tracked_literal_child_entries;
    std::unordered_map<int, int> tracked_child_root_by_register;
    std::unordered_map<int, std::vector<int>> tracked_child_aliases_by_root;
    bool child_alias_set_ambiguous = false;

    const auto register_literal_child_alias = [&](int source_register, int alias_register) {
        if (source_register < 0 || alias_register < 0 ||
            static_cast<std::size_t>(source_register) >= preview_values.size() ||
            static_cast<std::size_t>(alias_register) >= preview_values.size()) {
            return;
        }
        const auto source_root = tracked_child_root_by_register.find(source_register);
        if (source_root == tracked_child_root_by_register.end()) {
            return;
        }
        const int root = source_root->second;
        tracked_child_root_by_register[alias_register] = root;
        std::vector<int>& aliases = tracked_child_aliases_by_root[root];
        if (std::find(aliases.begin(), aliases.end(), source_register) == aliases.end()) {
            aliases.push_back(source_register);
        }
        if (std::find(aliases.begin(), aliases.end(), alias_register) == aliases.end()) {
            aliases.push_back(alias_register);
        }
        if (aliases.size() > kMaxChildAliasSetSize) {
            child_alias_set_ambiguous = true;
        }
    };
    const auto uses_table_alias = [&](std::string_view expression) {
        for (const std::string& alias_name : table_alias_names) {
            if (!alias_name.empty() && CountIdentifierUses(expression, alias_name) != 0) {
                return true;
            }
        }
        return false;
    };
    const auto refresh_tracked_literal_children = [&]() {
        if (tracked_literal_child_entries.empty()) {
            return true;
        }
        if (child_alias_set_ambiguous) {
            return false;
        }
        for (const TrackedLiteralChildEntry& tracked_entry : tracked_literal_child_entries) {
            if (tracked_entry.child_root_register < 0 ||
                tracked_entry.entry_index >= literal_entries.size()) {
                continue;
            }
            const auto alias_it =
                tracked_child_aliases_by_root.find(tracked_entry.child_root_register);
            const std::vector<int>* alias_registers =
                alias_it == tracked_child_aliases_by_root.end() ? nullptr : &alias_it->second;
            if (alias_registers != nullptr &&
                alias_registers->size() > kMaxChildAliasSetSize) {
                return false;
            }

            std::string selected_value;
            const auto try_select_value_for_register = [&](int register_index) {
                if (register_index < 0 ||
                    static_cast<std::size_t>(register_index) >= preview_values.size()) {
                    return false;
                }
                const StraightLineValue& tracked_value =
                    preview_values[static_cast<std::size_t>(register_index)];
                if (!tracked_value.valid ||
                    !IsSafeRenderedTableLiteralInitializerExpression(tracked_value.text) ||
                    uses_table_alias(tracked_value.text)) {
                    return false;
                }
                const std::string_view trimmed_value = TrimLuaWhitespace(tracked_value.text);
                if (trimmed_value.size() < 2 || trimmed_value.front() != '{' ||
                    trimmed_value.back() != '}') {
                    return false;
                }
                selected_value = tracked_value.text;
                return true;
            };

            bool selected = false;
            if (alias_registers != nullptr) {
                for (const int alias_register : *alias_registers) {
                    if (try_select_value_for_register(alias_register)) {
                        selected = true;
                        break;
                    }
                }
            }
            if (!selected) {
                selected = try_select_value_for_register(tracked_entry.child_root_register);
            }
            if (!selected) {
                // Conservative ownership: if we tracked a child-table field but cannot
                // re-materialize a concrete child literal, abort constructor folding.
                // This avoids committing partial literals that later drift into
                // malformed member chains (or nil placeholders) in large scripts.
                return false;
            }
            literal_entries[tracked_entry.entry_index] = RenderLuaTableLiteralField(
                tracked_entry.key_expression,
                selected_value,
                tracked_entry.array_index,
                nullptr);
        }
        return true;
    };
    const auto try_track_literal_child_entry =
        [&](int rhs_rk, std::string_view key_expression, int array_index) {
            if (IsConstantReference(rhs_rk) || rhs_rk < 0 ||
                static_cast<std::size_t>(rhs_rk) >= preview_values.size() ||
                literal_entries.empty()) {
                return;
            }
            const StraightLineValue& rhs_value =
                preview_values[static_cast<std::size_t>(rhs_rk)];
            if (!rhs_value.valid ||
                !IsSafeRenderedTableLiteralInitializerExpression(rhs_value.text) ||
                uses_table_alias(rhs_value.text)) {
                return;
            }
            const std::string_view trimmed_value = TrimLuaWhitespace(rhs_value.text);
            if (trimmed_value.size() < 2 || trimmed_value.front() != '{' ||
                trimmed_value.back() != '}') {
                return;
            }
            const auto existing_root = tracked_child_root_by_register.find(rhs_rk);
            const int tracked_root = existing_root == tracked_child_root_by_register.end()
                                         ? rhs_rk
                                         : existing_root->second;
            tracked_child_root_by_register[rhs_rk] = tracked_root;
            std::vector<int>& aliases = tracked_child_aliases_by_root[tracked_root];
            if (std::find(aliases.begin(), aliases.end(), rhs_rk) == aliases.end()) {
                aliases.push_back(rhs_rk);
            }
            if (aliases.size() > kMaxChildAliasSetSize) {
                child_alias_set_ambiguous = true;
            }
            tracked_literal_child_entries.push_back(
                TrackedLiteralChildEntry{
                    .entry_index = literal_entries.size() - 1,
                    .child_root_register = tracked_root,
                    .key_expression = std::string(key_expression),
                    .array_index = array_index,
                });
        };

    const auto try_append_table_constructor_entry =
        [&](const Lua51Instruction& candidate, int candidate_line, int* extra_words_out) {
            if (extra_words_out != nullptr) {
                *extra_words_out = 0;
            }
            if (candidate.opcode == Lua51Opcode::kSetTable &&
                register_is_table_alias(candidate.a)) {
                const StraightLineValue key =
                    GetRKValue(preview_values,
                               context.declarations,
                               context.function,
                               candidate.b,
                               candidate_line);
                const StraightLineValue rhs =
                    GetRKValue(preview_values,
                               context.declarations,
                               context.function,
                               candidate.c,
                               candidate_line);
                if (!key.valid || !rhs.valid ||
                    !IsSafeRenderedTableLiteralInitializerExpression(key.text) ||
                    !IsSafeRenderedTableLiteralInitializerExpression(rhs.text) ||
                    uses_table_alias(key.text) ||
                    uses_table_alias(rhs.text)) {
                    return false;
                }
                bool consumed_array_slot = false;
                const int entry_array_index = expected_array_index;
                literal_entries.push_back(RenderLuaTableLiteralField(
                    key.text, rhs.text, expected_array_index, &consumed_array_slot));
                if (consumed_array_slot) {
                    ++expected_array_index;
                }
                try_track_literal_child_entry(candidate.c,
                                              key.text,
                                              consumed_array_slot ? entry_array_index : -1);
                return true;
            }
            if (candidate.opcode == Lua51Opcode::kSetList &&
                register_is_table_alias(candidate.a)) {
                if (!context.resolve_setlist_block || !context.setlist_values_for) {
                    return false;
                }
                int extra_words = 0;
                const int block = context.resolve_setlist_block(
                    effective_current, candidate, &extra_words, nullptr);
                if (block < 0) {
                    return false;
                }
                const std::vector<StraightLineValue> entries =
                    context.setlist_values_for(preview_values,
                                               candidate.a,
                                               candidate.b,
                                               candidate_line);
                if (entries.empty()) {
                    return false;
                }
                for (const StraightLineValue& entry : entries) {
                    const std::string_view normalized_entry = TrimLuaWhitespace(entry.text);
                    // Reject unresolved setlist placeholders in constructor folding.
                    // If a slot is still rendered as nil here, ownership isn't strong
                    // enough for a stable literal commit.
                    if (normalized_entry.empty() || normalized_entry == "nil") {
                        return false;
                    }
                    if (uses_table_alias(entry.text)) {
                        return false;
                    }
                }
                for (std::size_t offset = 0; offset < entries.size(); ++offset) {
                    const StraightLineValue& entry = entries[offset];
                    const int entry_array_index = expected_array_index;
                    literal_entries.push_back(entry.text);
                    if (candidate.b > 0) {
                        try_track_literal_child_entry(candidate.a + 1 +
                                                          static_cast<int>(offset),
                                                      std::to_string(entry_array_index),
                                                      entry_array_index);
                    }
                    ++expected_array_index;
                }
                ++scoped_setlist_links;
                if (extra_words_out != nullptr) {
                    *extra_words_out = extra_words;
                }
                return true;
            }
            return false;
        };

    enum class TableConstructorSinkKind {
        kNone,
        kSetGlobal,
        kSetUpval,
        kSetTable,
        kReturn,
    };
    const auto classify_constructor_sink = [&](const Lua51Instruction& candidate,
                                               int owner_register,
                                               int* sink_owner_register_out) {
        const auto is_owner_alias = [&](int reg) {
            return reg == owner_register || register_is_table_alias(reg);
        };
        if (candidate.opcode == Lua51Opcode::kSetGlobal &&
            is_owner_alias(candidate.a)) {
            if (sink_owner_register_out != nullptr) {
                *sink_owner_register_out = candidate.a;
            }
            return TableConstructorSinkKind::kSetGlobal;
        }
        if (candidate.opcode == Lua51Opcode::kSetUpval &&
            is_owner_alias(candidate.a)) {
            if (sink_owner_register_out != nullptr) {
                *sink_owner_register_out = candidate.a;
            }
            return TableConstructorSinkKind::kSetUpval;
        }
        if (candidate.opcode == Lua51Opcode::kSetTable &&
            is_owner_alias(candidate.c) &&
            !register_is_table_alias(candidate.a)) {
            if (sink_owner_register_out != nullptr) {
                *sink_owner_register_out = candidate.c;
            }
            return TableConstructorSinkKind::kSetTable;
        }
        if (candidate.opcode == Lua51Opcode::kReturn &&
            candidate.a >= 0 &&
            candidate.b != 1) {
            auto return_range_contains_owner_alias = [&]() {
                if (candidate.b == 0) {
                    if (owner_register >= candidate.a) {
                        return true;
                    }
                    for (const int alias_register : table_alias_registers) {
                        if (alias_register >= candidate.a) {
                            return true;
                        }
                    }
                    return false;
                }
                const int return_end = candidate.a + candidate.b - 1;
                if (owner_register >= candidate.a && owner_register < return_end) {
                    return true;
                }
                for (const int alias_register : table_alias_registers) {
                    if (alias_register >= candidate.a && alias_register < return_end) {
                        return true;
                    }
                }
                return false;
            };
            if (!return_range_contains_owner_alias()) {
                if (sink_owner_register_out != nullptr) {
                    *sink_owner_register_out = -1;
                }
                return TableConstructorSinkKind::kNone;
            }
            if (sink_owner_register_out != nullptr) {
                *sink_owner_register_out = owner_register;
            }
            return TableConstructorSinkKind::kReturn;
        }
        if (sink_owner_register_out != nullptr) {
            *sink_owner_register_out = -1;
        }
        return TableConstructorSinkKind::kNone;
    };
    const auto has_recent_deepcopy_seed_for_sink_target =
        [&](const Lua51Instruction& sink_candidate) {
            if (sink_candidate.opcode != Lua51Opcode::kSetTable ||
                sink_candidate.a < 0 ||
                !IsConstantReference(sink_candidate.b)) {
                return false;
            }
            const auto is_deepcopy_global_load = [&](const Lua51Instruction& candidate,
                                                     int target_register) {
                if (candidate.opcode != Lua51Opcode::kGetGlobal ||
                    candidate.a != target_register ||
                    candidate.bx < 0 ||
                    static_cast<std::size_t>(candidate.bx) >=
                        context.function.constants.size()) {
                    return false;
                }
                const Lua51Constant& constant =
                    context.function.constants[static_cast<std::size_t>(candidate.bx)];
                return constant.type == Lua51ConstantType::kString &&
                       constant.string_value == "DeepCopyTable";
            };

            constexpr int kDeepCopySinkLookback = 48;
            constexpr int kDeepCopyProducerLookback = 8;
            const int lookback_begin = std::max(1, current - kDeepCopySinkLookback);
            for (int probe = current - 1; probe >= lookback_begin; --probe) {
                const Lua51Instruction& previous =
                    context.instructions[static_cast<std::size_t>(probe - 1)];
                if (IsLuaOwnershipControlFlowBarrier(previous)) {
                    break;
                }
                if (previous.opcode != Lua51Opcode::kSetTable ||
                    previous.a != sink_candidate.a ||
                    previous.b != sink_candidate.b ||
                    IsConstantReference(previous.c) ||
                    previous.c < 0) {
                    continue;
                }

                const int prior_value_register = previous.c;
                bool clobbered_before_constructor = false;
                for (int between = probe + 1; between < current; ++between) {
                    if (LuaInstructionClobbersTrackedRegister(
                            context.instructions[static_cast<std::size_t>(between - 1)],
                            prior_value_register)) {
                        clobbered_before_constructor = true;
                        break;
                    }
                }
                if (clobbered_before_constructor) {
                    continue;
                }

                const int producer_begin =
                    std::max(lookback_begin, probe - kDeepCopyProducerLookback);
                for (int producer = probe - 1; producer >= producer_begin; --producer) {
                    const Lua51Instruction& producer_instruction =
                        context.instructions[static_cast<std::size_t>(producer - 1)];
                    if (IsLuaOwnershipControlFlowBarrier(producer_instruction)) {
                        break;
                    }
                    if (!LuaInstructionClobbersTrackedRegister(producer_instruction,
                                                               prior_value_register)) {
                        continue;
                    }
                    if (producer_instruction.opcode == Lua51Opcode::kCall &&
                        producer_instruction.a == prior_value_register &&
                        producer_instruction.c == 2 &&
                        producer > 1 &&
                        is_deepcopy_global_load(
                            context.instructions[static_cast<std::size_t>(producer - 2)],
                            prior_value_register)) {
                        return true;
                    }
                    break;
                }
            }
            return false;
        };

    while (effective_current <= constructor_scan_end &&
           static_cast<std::size_t>(effective_current) <= context.instructions.size()) {
        const Lua51Instruction& candidate =
            context.instructions[static_cast<std::size_t>(effective_current - 1)];
        const int candidate_line = static_cast<int>(
            candidate.line == 0 ? effective_current : candidate.line);
        if (is_constructor_control_flow_barrier(candidate)) {
            return reject_scoped_constructor();
        }
        if (candidate.opcode == Lua51Opcode::kMove &&
            register_is_table_alias(candidate.b)) {
            register_literal_child_alias(candidate.b, candidate.a);
            if (!add_table_alias(candidate.a, candidate_line)) {
                return reject_scoped_constructor();
            }
            if (candidate.b == effective_register) {
                preserved_owner_registers.push_back(effective_register);
                effective_register = candidate.a;
            }
            ++effective_current;
            if (!refresh_tracked_literal_children()) {
                return reject_scoped_constructor();
            }
            continue;
        }
        int extra_words = 0;
        if (try_append_table_constructor_entry(candidate, candidate_line, &extra_words)) {
            effective_current += extra_words + 1;
            if (!refresh_tracked_literal_children()) {
                return reject_scoped_constructor();
            }
            continue;
        }
        int sink_owner_register = -1;
        if (classify_constructor_sink(candidate,
                                      effective_register,
                                      &sink_owner_register) !=
            TableConstructorSinkKind::kNone) {
            if (sink_owner_register >= 0) {
                effective_register = sink_owner_register;
            }
            break;
        }
        if (touches_table_alias_register(candidate)) {
            return reject_scoped_constructor();
        }
        if ((candidate.opcode == Lua51Opcode::kSetTable ||
             candidate.opcode == Lua51Opcode::kSetList) &&
            register_is_detached_table_alias(candidate.a)) {
            // Avoid partial detached-child folding; use fallback for this shape.
            return reject_scoped_constructor();
        }
        if (candidate.opcode == Lua51Opcode::kMove &&
            register_is_detached_table_alias(candidate.b)) {
            return reject_scoped_constructor();
        }
        if (candidate.opcode == Lua51Opcode::kNewTable &&
            (touches_table_alias_register(candidate) ||
             touches_detached_table_alias_register(candidate))) {
            return reject_scoped_constructor();
        }
        if (touches_detached_table_alias_register(candidate)) {
            return reject_scoped_constructor();
        }
        int next_current = effective_current + 1;
        if (TryPreviewLuaShortcutOperation(context,
                                           effective_current,
                                           candidate_line,
                                           LuaPreviewOperationMode::kSideEffectFreeOnly,
                                           &preview_values,
                                           &preview_declared,
                                           &next_current)) {
            if (next_current <= effective_current) {
                return reject_scoped_constructor();
            }
            effective_current = next_current;
            if (!refresh_tracked_literal_children()) {
                return reject_scoped_constructor();
            }
            continue;
        }
        break;
    }

    if (effective_current > constructor_scan_end ||
        static_cast<std::size_t>(effective_current) > context.instructions.size()) {
        return reject_scoped_constructor();
    }

    const Lua51Instruction& next =
        context.instructions[static_cast<std::size_t>(effective_current - 1)];
    const int next_line = static_cast<int>(next.line == 0 ? effective_current : next.line);
    int sink_owner_register = -1;
    const TableConstructorSinkKind sink_kind =
        classify_constructor_sink(next, effective_register, &sink_owner_register);
    if (sink_owner_register >= 0) {
        effective_register = sink_owner_register;
    }
    const bool can_use_segmented_local_sink =
        sink_kind == TableConstructorSinkKind::kNone &&
        precheck_segmented_local_sink &&
        allow_segmented_local_constructor_sink &&
        !touches_table_alias_register(next) &&
        !touches_detached_table_alias_register(next);
    if (!can_use_segmented_local_sink &&
        sink_kind == TableConstructorSinkKind::kNone) {
        return reject_scoped_constructor();
    }

    int owner_next_current = effective_current + 1;
    int post_sink_alias_moves = 0;
    int post_sink_constructor_entries = 0;
    const int post_sink_alias_move_limit =
        deep_constructor_parity_target ? kPostSinkAliasMoveLimit : 0;
    // Keep post-sink folding deterministic: only segmented-local sink paths can
    // continue absorbing constructor entries. Once a concrete sink is proven,
    // additional entries must fall back rather than speculate.
    const int post_sink_constructor_entry_limit =
        can_use_segmented_local_sink ? kPostSinkConstructorEntryLimit : 0;
    while (owner_next_current <= constructor_scan_end &&
           static_cast<std::size_t>(owner_next_current) <= context.instructions.size()) {
        const Lua51Instruction& candidate =
            context.instructions[static_cast<std::size_t>(owner_next_current - 1)];
        const int candidate_line = static_cast<int>(
            candidate.line == 0 ? owner_next_current : candidate.line);
        if (is_constructor_control_flow_barrier(candidate)) {
            return reject_scoped_constructor();
        }
        if (!touches_table_alias_register(candidate)) {
            break;
        }
        effective_current = owner_next_current;
        if (candidate.opcode == Lua51Opcode::kMove &&
            register_is_table_alias(candidate.b)) {
            if (post_sink_alias_moves >= post_sink_alias_move_limit) {
                return reject_scoped_constructor();
            }
            register_literal_child_alias(candidate.b, candidate.a);
            if (!add_table_alias(candidate.a, candidate_line)) {
                return reject_scoped_constructor();
            }
            if (candidate.b == effective_register) {
                preserved_owner_registers.push_back(effective_register);
                effective_register = candidate.a;
            }
            ++post_sink_alias_moves;
            const int previous_owner_next_current = owner_next_current;
            ++owner_next_current;
            if (!ensure_constructor_progress(previous_owner_next_current,
                                             owner_next_current)) {
                return reject_scoped_constructor();
            }
            if (!refresh_tracked_literal_children()) {
                return reject_scoped_constructor();
            }
            continue;
        }
        int post_sink_extra_words = 0;
        if (try_append_table_constructor_entry(
                candidate, candidate_line, &post_sink_extra_words)) {
            if (post_sink_constructor_entries >= post_sink_constructor_entry_limit) {
                return reject_scoped_constructor();
            }
            ++post_sink_constructor_entries;
            const int previous_owner_next_current = owner_next_current;
            owner_next_current += post_sink_extra_words + 1;
            if (!ensure_constructor_progress(previous_owner_next_current,
                                             owner_next_current)) {
                return reject_scoped_constructor();
            }
            if (!refresh_tracked_literal_children()) {
                return reject_scoped_constructor();
            }
            continue;
        }
        if (touches_table_alias_register(candidate)) {
            return reject_scoped_constructor();
        }
        break;
    }
    effective_current = owner_next_current - 1;

    if (!refresh_tracked_literal_children()) {
        return reject_scoped_constructor();
    }
    const std::string finalized_literal_expression =
        RenderLuaMixedTableLiteralExpression(literal_entries);

    std::vector<LuaValueOperation> preserved_table_values;
    for (const int preserved_reg : preserved_owner_registers) {
        if (preserved_reg < 0) {
            continue;
        }
        preserved_table_values.push_back(
            LuaValueOperation{
                .reg = preserved_reg,
                .expression = finalized_literal_expression,
                .multiple_results = false,
            });
    }
    for (const int alias_reg : table_alias_registers) {
        if (alias_reg == effective_register || alias_reg < 0) {
            continue;
        }
        const bool already_preserved = std::find_if(
            preserved_table_values.begin(),
            preserved_table_values.end(),
            [&](const LuaValueOperation& value) { return value.reg == alias_reg; }) !=
                                     preserved_table_values.end();
        if (!already_preserved) {
            preserved_table_values.push_back(
                LuaValueOperation{
                    .reg = alias_reg,
                    .expression = finalized_literal_expression,
                    .multiple_results = false,
                });
        }
    }

    if (can_use_segmented_local_sink) {
        *operation = LuaBuiltOperation{
            .next_current = effective_current,
            .stop_render = false,
            .post_apply_values = std::move(preserved_table_values),
            .payload =
                LuaAssignmentOperation{
                    .first_reg = effective_register,
                    .count = 1,
                    .line = line,
                    .expression = finalized_literal_expression,
                    .multiple_results = false,
                },
        };
        ++g_lua_native_table_constructor_commits;
        g_lua_native_setlist_constructor_links += scoped_setlist_links;
        return true;
    }

    if (next.opcode == Lua51Opcode::kSetGlobal && next.a == effective_register) {
        if (next.bx < 0 ||
            static_cast<std::size_t>(next.bx) >= context.function.constants.size() ||
            context.function.constants[static_cast<std::size_t>(next.bx)].type !=
                Lua51ConstantType::kString) {
            return reject_scoped_constructor();
        }
        *operation = LuaBuiltOperation{
            .next_current = owner_next_current,
            .stop_render = false,
            .post_apply_values = std::move(preserved_table_values),
            .payload =
                LuaGlobalSetOperation{
                    .global_name = RenderGlobalExpression(
                        context.function.constants[static_cast<std::size_t>(next.bx)].string_value),
                    .expression = finalized_literal_expression,
                    .source_reg = effective_register,
                    .source_expression_override = finalized_literal_expression,
                    .source_multiple_results_override = false,
                },
        };
        ++g_lua_native_table_constructor_commits;
        g_lua_native_setlist_constructor_links += scoped_setlist_links;
        return true;
    }

    if (next.opcode == Lua51Opcode::kSetUpval && next.a == effective_register) {
        *operation = LuaBuiltOperation{
            .next_current = owner_next_current,
            .stop_render = false,
            .post_apply_values = std::move(preserved_table_values),
            .payload =
                LuaUpvalueSetOperation{
                    .upvalue_name = RenderUpvalueExpression(context.function, next.b),
                    .expression = finalized_literal_expression,
                    .source_reg = effective_register,
                    .source_expression_override = finalized_literal_expression,
                    .source_multiple_results_override = false,
                },
        };
        ++g_lua_native_table_constructor_commits;
        g_lua_native_setlist_constructor_links += scoped_setlist_links;
        return true;
    }

    if (next.opcode == Lua51Opcode::kSetTable && next.c == effective_register) {
        if (has_recent_deepcopy_seed_for_sink_target(next)) {
            // Preserve DeepCopyTable patch/continuation semantics by falling
            // back when the same table field was recently assigned from a
            // DeepCopyTable result before this constructor candidate.
            return reject_scoped_constructor();
        }
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
        const std::string resolved_table_expression =
            ResolveLuaTableExpressionForSetTarget(context,
                                                  preview_values,
                                                  next.a,
                                                  next_line,
                                                  effective_current - 1);
        const bool declare_local =
            next.a >= context.function.parameter_count &&
            context.declarations.Get(next.a, next_line) == nullptr &&
            (context.declared == nullptr ||
             (next.a >= 0 &&
              static_cast<std::size_t>(next.a) < context.declared->size() &&
              !(*context.declared)[static_cast<std::size_t>(next.a)]));
        *operation = LuaBuiltOperation{
            .next_current = owner_next_current,
            .stop_render = false,
            .post_apply_values = std::move(preserved_table_values),
            .payload =
                LuaTableSetOperation{
                    .table_expression = resolved_table_expression,
                    .key_expression = key.text,
                    .expression = finalized_literal_expression,
                    .fallback_name = GetStraightLineAssignmentRegisterName(
                        context.declarations, context.function, next.a, next_line),
                    .declare_local = declare_local,
                    .target_reg = next.a,
                    .source_reg = effective_register,
                    .source_expression_override = finalized_literal_expression,
                    .source_multiple_results_override = false,
                },
        };
        ++g_lua_native_table_constructor_commits;
        g_lua_native_setlist_constructor_links += scoped_setlist_links;
        return true;
    }

    if (next.opcode == Lua51Opcode::kReturn && next.a == effective_register && next.b == 2) {
        *operation = LuaBuiltOperation{
            .next_current = effective_current + 1,
            .stop_render = true,
            .payload =
                LuaReturnOperation{
                    .expression = finalized_literal_expression,
                    .has_values = true,
                },
        };
        ++g_lua_native_table_constructor_commits;
        g_lua_native_setlist_constructor_links += scoped_setlist_links;
        return true;
    }

    return reject_scoped_constructor();
}

bool TryBuildLuaDirectTableLiteralOperation(const LuaOperationBuildContext& context,
                                            int current,
                                            int line,
                                            std::string* error,
                                            LuaBuiltOperation* operation) {
    if (current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
        return false;
    }
    const Lua51Instruction& instruction =
        context.instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kNewTable) {
        return false;
    }
    bool is_combatstyles_scope = false;
    bool use_high_budget_scope = false;
    const bool top_level_constructor_scope = context.function.line_defined == 0;
    const bool is_enhanced_scope =
        top_level_constructor_scope &&
        IsLuaEnhancedTopLevelConstructorScope(
            context.function, &is_combatstyles_scope, &use_high_budget_scope);
    const LuaScriptPolicy script_policy = BuildLuaScriptPolicy(
        context.function, static_cast<int>(context.instructions.size()));
    constexpr int kTopLevelConstructorAttemptBudgetDefault = 4;
    constexpr int kNestedConstructorAttemptBudgetDefault = 2;
    constexpr int kNestedConstructorInstructionBudget = 1024;
    static thread_local const Lua51Function* budget_function = nullptr;
    static thread_local int budget_remaining = 0;

    if (!top_level_constructor_scope &&
        static_cast<int>(context.instructions.size()) > kNestedConstructorInstructionBudget) {
        return TryBuildLuaDirectTableLiteralOperationLegacy(
            context, current, line, error, operation);
    }

    const int scoped_budget =
        top_level_constructor_scope
            ? (is_enhanced_scope
                   ? std::max(1, script_policy.constructor_attempt_budget)
                   : kTopLevelConstructorAttemptBudgetDefault)
            : kNestedConstructorAttemptBudgetDefault;
    if (&context.function != budget_function) {
        budget_function = &context.function;
        budget_remaining = scoped_budget;
    }
    if (budget_remaining > 0) {
        --budget_remaining;
        if (TryBuildLuaDirectTableLiteralOperationOwnedConstructor(
                context, current, line, error, operation)) {
            return true;
        }
    }
    return TryBuildLuaDirectTableLiteralOperationLegacy(
        context, current, line, error, operation);
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
        const std::string resolved_table_expression =
            ResolveLuaTableExpressionForSetTarget(context,
                                                  *context.values,
                                                  next.a,
                                                  next_line,
                                                  effective_current - 1);
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
                    .table_expression = resolved_table_expression,
                    .key_expression = key.text,
                    .expression = assignment.expression,
                    .fallback_name = GetStraightLineAssignmentRegisterName(
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
                                            LuaPreviewOperationMode::kSideEffectFreeOnly,
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
        RenderCallExpression(callee,
                             arguments,
                             context.declarations,
                             context.function,
                             maybe_call_current,
                             context.register_count),
        true,
        false}};
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
            if (context.allow_opaque_preview_closures ||
                context.allow_opaque_nested_closure_fallback) {
                *assignment = LuaAssignmentOperation{
                    .first_reg = instruction.a,
                    .count = 1,
                    .line = line,
                    .expression = BuildLuaOpaqueNestedClosureExpression(context.function,
                                                                        instruction.bx),
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
        .expression = RenderCallExpression(callee,
                                           arguments,
                                           context.declarations,
                                           context.function,
                                           line,
                                           context.register_count),
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
    if (results.empty() && current == end_line &&
        current == static_cast<int>(context.instructions.size())) {
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
                                  int current,
                                  int line,
                                  LuaTableSetOperation* operation) {
    if (operation == nullptr || context.values == nullptr ||
        instruction.opcode != Lua51Opcode::kSetTable) {
        return false;
    }
    const StraightLineValue key =
        GetRKValue(*context.values, context.declarations, context.function, instruction.b, line);
    const StraightLineValue rhs =
        GetRKValue(*context.values, context.declarations, context.function, instruction.c, line);
    const std::string resolved_table_expression =
        ResolveLuaTableExpressionForSetTarget(context,
                                              *context.values,
                                              instruction.a,
                                              line,
                                              current);
    const bool declare_local =
        instruction.a >= context.function.parameter_count &&
        context.declarations.Get(instruction.a, line) == nullptr &&
        (context.declared == nullptr ||
         (instruction.a >= 0 &&
          static_cast<std::size_t>(instruction.a) < context.declared->size() &&
          !(*context.declared)[static_cast<std::size_t>(instruction.a)]));
    *operation = LuaTableSetOperation{
        .table_expression = resolved_table_expression,
        .key_expression = key.text,
        .expression = rhs.text,
        .fallback_name = GetStraightLineAssignmentRegisterName(
            context.declarations, context.function, instruction.a, line),
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

    const std::string resolved_table_expression =
        ResolveLuaTableExpressionForSetTarget(context,
                                              *context.values,
                                              instruction.a,
                                              line,
                                              current);
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
            RenderAssignmentStatement(resolved_table_expression,
                                      RenderTableLiteralExpression(entries));
        return true;
    }

    *operation = LuaSetListOperation{
        .table_expression = resolved_table_expression,
        .base_index = base,
        .entries = entries,
        .try_inline_initializer = true,
        .target_reg = instruction.a,
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
    if (context.allow_cross_line_shortcuts) {
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
            current,
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

void ResetLuaNativeTableConstructorCounters() {
    g_lua_native_table_constructor_commits = 0;
    g_lua_native_setlist_constructor_links = 0;
    g_lua_native_table_constructor_fallbacks = 0;
}

std::size_t ConsumeLuaNativeTableConstructorCommitCounter() {
    const std::size_t value = g_lua_native_table_constructor_commits;
    g_lua_native_table_constructor_commits = 0;
    return value;
}

std::size_t ConsumeLuaNativeSetListConstructorLinkCounter() {
    const std::size_t value = g_lua_native_setlist_constructor_links;
    g_lua_native_setlist_constructor_links = 0;
    return value;
}

std::size_t ConsumeLuaNativeTableConstructorFallbackCounter() {
    const std::size_t value = g_lua_native_table_constructor_fallbacks;
    g_lua_native_table_constructor_fallbacks = 0;
    return value;
}

}  // namespace refii::lua
