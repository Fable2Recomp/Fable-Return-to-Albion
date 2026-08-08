#include "tools/native/straight_line_native_render.h"
#include "tools/decompiler/branch_render.h"
#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/control_flow_graph.h"
#include "tools/decompiler/jump_render.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/loop_render.h"
#include "tools/decompiler/preview_render.h"
#include "tools/decompiler/render_state.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/statement_render.h"
#include "tools/decompiler/straight_line_render.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <cstdlib>
#include <functional>
#include <iostream>
#include <sstream>
#include <utility>

namespace refii::lua {
namespace {

constexpr int kLua51MaxStack = 250;
constexpr std::size_t kLargeCleanupInstructionThreshold = 6000;
constexpr std::size_t kLargeCleanupLineThreshold = 2500;

bool IsConditionalOpcode(Lua51Opcode opcode) {
    return decompiler::IsConditionalBranchOpcode(opcode);
}

bool LooksLikeRenderedTemporaryIdentifier(std::string_view identifier) {
    if (identifier.size() >= 2 && identifier.front() == 'r' &&
        std::isdigit(static_cast<unsigned char>(identifier[1])) != 0) {
        return true;
    }
    if (identifier.size() >= 3 && identifier.front() == 'l' && identifier[1] == '_' &&
        std::isdigit(static_cast<unsigned char>(identifier[2])) != 0) {
        return true;
    }
    if (identifier.size() >= 2 &&
        (identifier.front() == 'L' || identifier.front() == 'A') &&
        std::isdigit(static_cast<unsigned char>(identifier[1])) != 0) {
        return true;
    }
    return false;
}

bool LineHasRenderedTemporaryCandidate(std::string_view line) {
    const std::string_view stripped = StripLuaLineComment(line);
    for (std::size_t i = 0; i < stripped.size();) {
        const unsigned char ch = static_cast<unsigned char>(stripped[i]);
        if (std::isalpha(ch) == 0 && stripped[i] != '_') {
            ++i;
            continue;
        }

        const std::size_t start = i;
        ++i;
        while (i < stripped.size()) {
            const unsigned char inner = static_cast<unsigned char>(stripped[i]);
            if (std::isalnum(inner) == 0 && stripped[i] != '_') {
                break;
            }
            ++i;
        }

        if (LooksLikeRenderedTemporaryIdentifier(stripped.substr(start, i - start))) {
            return true;
        }
    }
    return false;
}

struct LuaRenderedCleanupFeatures {
    bool has_temporary_candidates = false;
    bool has_table_builder_candidates = false;
    bool has_conditional_iife_candidates = false;
    bool has_call_parenthesis_candidates = false;
};

LuaRenderedCleanupFeatures ScanRenderedLuaCleanupFeatures(
    const std::vector<std::string>& lines) {
    LuaRenderedCleanupFeatures features;
    for (const std::string& line : lines) {
        const std::string_view stripped = StripLuaLineComment(line);
        if (!features.has_temporary_candidates &&
            LineHasRenderedTemporaryCandidate(stripped)) {
            features.has_temporary_candidates = true;
        }
        if (!features.has_table_builder_candidates &&
            (stripped.find(" = {}") != std::string_view::npos ||
             stripped.find(" = {") != std::string_view::npos ||
             stripped.find("= {}") != std::string_view::npos ||
             stripped.find("= {") != std::string_view::npos)) {
            features.has_table_builder_candidates = true;
        }
        if (!features.has_conditional_iife_candidates &&
            stripped.find("(function()") != std::string_view::npos) {
            features.has_conditional_iife_candidates = true;
        }
        if (!features.has_call_parenthesis_candidates &&
            stripped.find(")(") != std::string_view::npos) {
            features.has_call_parenthesis_candidates = true;
        }
        if (features.has_temporary_candidates &&
            features.has_table_builder_candidates &&
            features.has_conditional_iife_candidates &&
            features.has_call_parenthesis_candidates) {
            break;
        }
    }
    return features;
}

template <typename PassFn>
bool RunBoundedRenderPassLoop(int max_passes, PassFn&& run_single_pass) {
    bool changed = false;
    const int pass_cap = std::max(max_passes, 1);
    for (int pass = 0; pass < pass_cap; ++pass) {
        if (!run_single_pass(pass)) {
            break;
        }
        changed = true;
    }
    return changed;
}

bool HasValidRenderedLuaBlockBalance(const std::vector<std::string>& lines) {
    int balance = 0;
    for (const std::string& line : lines) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (trimmed.empty()) {
            continue;
        }

        balance += GetRenderedLuaBlockDelta(trimmed);
        if (balance < 0) {
            return false;
        }
    }

    return balance == 0;
}

std::string BuildLua51OpaqueFunctionStubSignature(const Lua51Function& function) {
    std::vector<Lua51Declaration> declarations = InferLua51Declarations(function);
    RegisterDeclarations declaration_map(
        static_cast<int>(std::min<std::size_t>(function.max_stack_size, kLua51MaxStack)),
        static_cast<int>(function.code.size()),
        &declarations);

    std::string signature = "function(";
    for (int reg = 0; reg < function.parameter_count; ++reg) {
        if (reg != 0) {
            signature += ", ";
        }
        signature += GetStraightLineRegisterName(declaration_map, function, reg, 1);
    }
    if (function.vararg_flags != 0) {
        if (function.parameter_count != 0) {
            signature += ", ";
        }
        signature += "...";
    }
    signature += ")";
    return signature;
}

bool RenderLua51OpaqueNestedClosureStub(const Lua51Function& function,
                                        std::string* output,
                                        std::string* error) {
    if (output == nullptr) {
        if (error != nullptr) {
            *error = "render_output_missing";
        }
        return false;
    }

    *output = BuildLua51OpaqueFunctionStubSignature(function);
    output->append("\nend\n");
    if (error != nullptr) {
        error->clear();
    }
    return true;
}

}  // namespace

bool RenderLua51StraightLineFunctionImpl(const Lua51Function& function,
                                         bool wrap_function,
                                         bool simplify_rendered_output,
                                         bool allow_opaque_nested_child_closure_stubs,
                                         std::string* output,
                                         std::string* error) {
    if (output == nullptr) {
        if (error) {
            *error = "render_output_missing";
        }
        return false;
    }

    const auto deadline_exceeded = [&]() {
        if (!internal::IsCurrentDecompileRepairDeadlineExceeded()) {
            return false;
        }
        if (error != nullptr) {
            *error = "deadline_exceeded";
        }
        return true;
    };
    if (deadline_exceeded()) {
        return false;
    }

    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    const decompiler::Lua51ControlFlowGraph control_flow_graph =
        decompiler::BuildLua51ControlFlowGraph(instructions);
    for (std::size_t i = 0; i < instructions.size(); ++i) {
        if (deadline_exceeded()) {
            return false;
        }
        const Lua51Instruction& instruction = instructions[i];
        if (IsUnsupportedStraightLineOpcode(instruction.opcode) ||
            (instruction.opcode == Lua51Opcode::kLoadBool && instruction.c != 0 &&
             !IsLoadBoolPair(instructions, static_cast<int>(i + 1)))) {
            if (error) {
                *error = "unsupported_control_flow@pc=" + std::to_string(i + 1) +
                         ",opcode=" + std::to_string(static_cast<int>(instruction.opcode));
            }
            return false;
        }
    }

    std::vector<Lua51Declaration> declarations = InferLua51Declarations(function);
    RegisterDeclarations declaration_map(
        static_cast<int>(std::min<std::size_t>(function.max_stack_size, kLua51MaxStack)),
        static_cast<int>(function.code.size()),
        &declarations);

    const int register_count =
        static_cast<int>(std::min<std::size_t>(function.max_stack_size, kLua51MaxStack));
    std::vector<StraightLineValue> values(static_cast<std::size_t>(register_count));
    std::vector<bool> declared(static_cast<std::size_t>(register_count), false);

    std::vector<std::string> lines;
    std::vector<std::size_t> instruction_output_offsets(instructions.size() + 2, 0);
    if (wrap_function) {
        std::string header = "function(";
        for (int reg = 0; reg < function.parameter_count; ++reg) {
            if (reg != 0) {
                header += ", ";
            }
            const std::string name = GetStraightLineRegisterName(declaration_map, function, reg, 1);
            declared[static_cast<std::size_t>(reg)] = true;
            values[static_cast<std::size_t>(reg)] = StraightLineValue{name, true, false};
            header += name;
        }
        if (function.vararg_flags != 0) {
            if (function.parameter_count != 0) {
                header += ", ";
            }
            header += "...";
        }
        header += ")";
        lines.push_back(std::move(header));
    } else {
        for (int reg = 0; reg < function.parameter_count; ++reg) {
            const std::string name = GetStraightLineRegisterName(declaration_map, function, reg, 1);
            declared[static_cast<std::size_t>(reg)] = true;
            values[static_cast<std::size_t>(reg)] = StraightLineValue{name, true, false};
        }
    }

    for (int reg = function.parameter_count; reg < register_count; ++reg) {
        const Lua51Declaration* declaration = declaration_map.Get(reg, 0);
        if (declaration == nullptr || declaration->temporary || declaration->guessed ||
            declaration->begin != 0 || !IsValidLuaIdentifier(declaration->name)) {
            continue;
        }

        AppendIndentedStatement(&lines, "  ", "local " + declaration->name);
        declared[static_cast<std::size_t>(reg)] = true;
        values[static_cast<std::size_t>(reg)] =
            StraightLineValue{declaration->name, true, false};
    }

    const auto call_arguments_for = [&](const std::vector<StraightLineValue>& state_values,
                                        int a,
                                        int b,
                                        int line) {
        std::vector<StraightLineValue> arguments;
        if (b == 1) {
            return arguments;
        }
        if (b == 0) {
            for (int reg = a + 1; reg < register_count; ++reg) {
                const StraightLineValue argument =
                    GetRegisterValue(state_values, declaration_map, function, reg, line);
                if (!argument.valid) {
                    break;
                }
                arguments.push_back(argument);
                if (argument.multiple_results) {
                    break;
                }
            }
            return arguments;
        }
        for (int reg = a + 1; reg <= a + b - 1 && reg < register_count; ++reg) {
            arguments.push_back(
                GetRegisterValue(state_values, declaration_map, function, reg, line));
        }
        return arguments;
    };
    const auto return_values_for = [&](const std::vector<StraightLineValue>& state_values,
                                       int a,
                                       int b,
                                       int line) {
        std::vector<StraightLineValue> results;
        if (b == 1) {
            return results;
        }
        if (b == 0) {
            for (int reg = a; reg < register_count; ++reg) {
                const StraightLineValue value =
                    GetRegisterValue(state_values, declaration_map, function, reg, line);
                if (!value.valid) {
                    break;
                }
                results.push_back(value);
                if (value.multiple_results) {
                    break;
                }
            }
            return results;
        }
        for (int reg = a; reg <= a + b - 2 && reg < register_count; ++reg) {
            results.push_back(
                GetRegisterValue(state_values, declaration_map, function, reg, line));
        }
        return results;
    };
    LuaPreviewRenderCache preview_cache;

    const auto try_recover_guarded_fallback_setlist_value =
        [&](const std::vector<StraightLineValue>& state_values,
            int target_reg,
            int use_line,
            StraightLineValue* recovered) {
            if (recovered == nullptr || use_line < 1 ||
                static_cast<std::size_t>(use_line) > instructions.size()) {
                return false;
            }

            const LuaPreviewRenderContext preview_context{
                .instructions = instructions,
                .control_flow_graph = &control_flow_graph,
                .declarations = declaration_map,
                .function = function,
                .register_count = register_count,
                .call_arguments_for = call_arguments_for,
                .render_child = {},
                .cache = &preview_cache,
            };

            const auto evaluate_value_at_line =
                [&](int start_line,
                    int end_line,
                    int target_line,
                    StraightLineValue* value_out) -> bool {
                    if (value_out == nullptr || start_line < 1 || end_line < start_line ||
                        static_cast<std::size_t>(end_line) > instructions.size()) {
                        return false;
                    }
                    std::vector<StraightLineValue> preview_values = state_values;
                    std::vector<bool> preview_declared(
                        static_cast<std::size_t>(register_count), false);
                    std::string preview_error;
                    if (!EvaluateLuaInlineRange(preview_context,
                                                start_line,
                                                end_line,
                                                &preview_values,
                                                &preview_declared,
                                                &preview_error)) {
                        return false;
                    }
                    const StraightLineValue value = GetRegisterValue(
                        preview_values,
                        declaration_map,
                        function,
                        target_reg,
                        target_line);
                    if (!value.valid) {
                        return false;
                    }
                    *value_out = value;
                    return true;
                };

            const int instruction_count = static_cast<int>(instructions.size());
            const int scan_begin = std::max(1, use_line - 24);
            const int latest_join_line = use_line;
            for (int test_line = use_line - 2; test_line >= scan_begin; --test_line) {
                if (test_line + 1 > instruction_count) {
                    continue;
                }
                const Lua51Instruction& target_test =
                    instructions[static_cast<std::size_t>(test_line - 1)];
                const Lua51Instruction& target_jump =
                    instructions[static_cast<std::size_t>(test_line)];
                if (target_test.opcode != Lua51Opcode::kTest || target_test.a != target_reg ||
                    target_test.c != 1 || target_jump.opcode != Lua51Opcode::kJmp) {
                    continue;
                }
                const int join_line = decompiler::ComputeJumpTargetLine(
                    test_line + 1, target_jump.sbx, instruction_count);
                if (join_line < test_line + 2 || join_line > latest_join_line) {
                    continue;
                }

                const int fallback_start = test_line + 2;
                const int fallback_end = join_line - 1;
                if (fallback_start > fallback_end) {
                    continue;
                }

                const std::vector<bool> preserved_registers =
                    CollectWrittenLuaRegistersInRange(instructions,
                                                      register_count,
                                                      join_line,
                                                      use_line - 1);
                if (static_cast<std::size_t>(target_reg) < preserved_registers.size() &&
                    preserved_registers[static_cast<std::size_t>(target_reg)]) {
                    continue;
                }

                for (int outer_test_line = test_line - 2; outer_test_line >= scan_begin;
                     --outer_test_line) {
                    if (outer_test_line + 1 > instruction_count) {
                        continue;
                    }
                    const Lua51Instruction& outer_test =
                        instructions[static_cast<std::size_t>(outer_test_line - 1)];
                    const Lua51Instruction& outer_jump =
                        instructions[static_cast<std::size_t>(outer_test_line)];
                    if (outer_test.opcode != Lua51Opcode::kTest ||
                        outer_jump.opcode != Lua51Opcode::kJmp) {
                        continue;
                    }
                    const int outer_target = decompiler::ComputeJumpTargetLine(
                        outer_test_line + 1, outer_jump.sbx, instruction_count);
                    if (outer_target != fallback_start) {
                        continue;
                    }

                    const int positive_start = outer_test_line + 2;
                    const int positive_end = test_line - 1;
                    if (positive_start > positive_end) {
                        continue;
                    }

                    StraightLineValue positive_value;
                    StraightLineValue fallback_value;
                    if (!evaluate_value_at_line(
                            positive_start, positive_end, positive_end, &positive_value) ||
                        !evaluate_value_at_line(
                            fallback_start, fallback_end, fallback_end, &fallback_value)) {
                        continue;
                    }

                    const StraightLineValue condition_value =
                        GetRegisterValue(state_values,
                                         declaration_map,
                                         function,
                                         outer_test.a,
                                         outer_test_line);
                    if (!condition_value.valid) {
                        continue;
                    }

                    std::string condition_text = condition_value.text;
                    if (outer_test.c != 0) {
                        condition_text =
                            "(not " + WrapInlineExpression(condition_text) + ")";
                    }

                    recovered->text = "((" + condition_text + " and " +
                                      WrapInlineExpression(positive_value.text) + ") or " +
                                      WrapInlineExpression(fallback_value.text) + ")";
                    recovered->valid = true;
                    recovered->multiple_results = false;
                    return true;
                }
            }

            return false;
        };

    const auto setlist_values_for = [&](const std::vector<StraightLineValue>& state_values,
                                        int a,
                                        int b,
                                        int line) {
        const auto has_recent_table_builder_chain = [&](int reg) {
            if (reg < 0) {
                return false;
            }

            int builder_anchor = -1;
            bool saw_builder_mutation = false;
            for (std::size_t instruction_index = 0;
                 instruction_index < instructions.size();
                 ++instruction_index) {
                const Lua51Instruction& instruction = instructions[instruction_index];
                const int instruction_line = static_cast<int>(
                    instruction.line == 0 ? instruction_index + 1 : instruction.line);
                if (instruction_line > line) {
                    break;
                }

                if (instruction.opcode == Lua51Opcode::kNewTable &&
                    instruction.a == reg) {
                    builder_anchor = static_cast<int>(instruction_index);
                    saw_builder_mutation = false;
                    continue;
                }

                if (builder_anchor < 0) {
                    continue;
                }

                if ((instruction.opcode == Lua51Opcode::kSetTable ||
                     instruction.opcode == Lua51Opcode::kSetList) &&
                    instruction.a == reg) {
                    saw_builder_mutation = true;
                    continue;
                }

                if (instruction.a == reg &&
                    instruction.opcode != Lua51Opcode::kSetTable &&
                    instruction.opcode != Lua51Opcode::kSetList) {
                    builder_anchor = -1;
                    saw_builder_mutation = false;
                }
            }

            return builder_anchor >= 0 && saw_builder_mutation;
        };
        const auto try_recover_setlist_register_identity =
            [&](StraightLineValue* value, int reg) {
                if (value == nullptr) {
                    return;
                }
                if (!value->valid || TrimLuaWhitespace(value->text) == "nil") {
                    StraightLineValue recovered;
                    if (try_recover_guarded_fallback_setlist_value(
                            state_values, reg, line, &recovered)) {
                        *value = std::move(recovered);
                    }
                }
                if ((!value->valid || TrimLuaWhitespace(value->text) == "nil") &&
                    has_recent_table_builder_chain(reg)) {
                    value->valid = true;
                    value->text = "{}";
                    value->multiple_results = false;
                }
            };

        std::vector<StraightLineValue> entries;
        if (b == 0) {
            for (int reg = a + 1; reg < register_count; ++reg) {
                StraightLineValue value =
                    GetRegisterValue(state_values, declaration_map, function, reg, line);
                try_recover_setlist_register_identity(&value, reg);
                if (!value.valid) {
                    break;
                }
                entries.push_back(value);
                if (value.multiple_results) {
                    break;
                }
            }
            return entries;
        }

        for (int reg = a + 1; reg <= a + b && reg < register_count; ++reg) {
            StraightLineValue value =
                GetRegisterValue(state_values, declaration_map, function, reg, line);
            try_recover_setlist_register_identity(&value, reg);
            entries.push_back(std::move(value));
        }
        return entries;
    };

    const auto resolve_setlist_block = [&](int current,
                                           const Lua51Instruction& instruction,
                                           int* extra_words,
                                           std::string* local_error) {
        if (extra_words != nullptr) {
            *extra_words = 0;
        }

        if (instruction.c != 0) {
            return instruction.c;
        }

        if (current >= static_cast<int>(instructions.size())) {
            if (local_error != nullptr) {
                *local_error = "unsupported_setlist_extraarg";
            }
            return -1;
        }

        if (extra_words != nullptr) {
            *extra_words = 1;
        }
        return static_cast<int>(instructions[static_cast<std::size_t>(current)].raw);
    };

    struct RenderedLuaChildCacheEntry {
        bool attempted = false;
        bool success = false;
        std::string rendered_source;
        std::string failure_error;
    };
    const bool cache_rendered_child_sources =
        instructions.size() <= 6000 && function.child_functions.size() <= 64;
    std::vector<RenderedLuaChildCacheEntry> rendered_child_cache(
        cache_rendered_child_sources ? function.child_functions.size() : 0);
    auto render_child = [&](int child_index, std::string* rendered_child) {
        if (deadline_exceeded()) {
            return false;
        }
        if (child_index < 0 ||
            static_cast<std::size_t>(child_index) >= function.child_functions.size()) {
            return false;
        }

        if (!cache_rendered_child_sources) {
            std::string local_rendered_child;
            std::string local_error;
            bool success = RenderLua51StraightLineFunctionImpl(
                function.child_functions[static_cast<std::size_t>(child_index)],
                true,
                simplify_rendered_output,
                allow_opaque_nested_child_closure_stubs,
                &local_rendered_child,
                &local_error);
            if (!success && simplify_rendered_output) {
                success = RenderLua51StraightLineFunctionImpl(
                    function.child_functions[static_cast<std::size_t>(child_index)],
                    true,
                    false,
                    allow_opaque_nested_child_closure_stubs,
                    &local_rendered_child,
                    &local_error);
            }
            if (!success && allow_opaque_nested_child_closure_stubs &&
                local_error == "unsupported_nested_closure") {
                if (deadline_exceeded()) {
                    return false;
                }
                success = RenderLua51OpaqueNestedClosureStub(
                    function.child_functions[static_cast<std::size_t>(child_index)],
                    &local_rendered_child,
                    &local_error);
            }
            if (!success) {
                if (error != nullptr && !local_error.empty()) {
                    *error = local_error;
                }
                return false;
            }
            if (rendered_child != nullptr) {
                *rendered_child = std::move(local_rendered_child);
            }
            return true;
        }

        RenderedLuaChildCacheEntry& cache_entry =
            rendered_child_cache[static_cast<std::size_t>(child_index)];
        if (!cache_entry.attempted) {
            if (deadline_exceeded()) {
                return false;
            }
            cache_entry.attempted = true;
            cache_entry.success = RenderLua51StraightLineFunctionImpl(
                function.child_functions[static_cast<std::size_t>(child_index)],
                true,
                simplify_rendered_output,
                allow_opaque_nested_child_closure_stubs,
                &cache_entry.rendered_source,
                &cache_entry.failure_error);
            if (!cache_entry.success && simplify_rendered_output) {
                cache_entry.success = RenderLua51StraightLineFunctionImpl(
                    function.child_functions[static_cast<std::size_t>(child_index)],
                    true,
                    false,
                    allow_opaque_nested_child_closure_stubs,
                    &cache_entry.rendered_source,
                    &cache_entry.failure_error);
            }
            if (!cache_entry.success && allow_opaque_nested_child_closure_stubs &&
                cache_entry.failure_error == "unsupported_nested_closure") {
                if (deadline_exceeded()) {
                    return false;
                }
                cache_entry.success = RenderLua51OpaqueNestedClosureStub(
                    function.child_functions[static_cast<std::size_t>(child_index)],
                    &cache_entry.rendered_source,
                    &cache_entry.failure_error);
            }
        }
        if (!cache_entry.success) {
            if (error != nullptr && !cache_entry.failure_error.empty()) {
                *error = cache_entry.failure_error;
            }
            return false;
        }
        if (rendered_child != nullptr) {
            *rendered_child = cache_entry.rendered_source;
        }
        return true;
    };
    const LuaRenderStateContext render_state_context{
        .instructions = instructions,
        .declarations = declaration_map,
        .function = function,
        .register_count = register_count,
    };

    const auto emit_new_local_declarations = [&](int pc) {
        EmitLuaNewLocalDeclarations(
            &lines, &values, &declared, render_state_context, pc);
    };

    const auto invalidate_values = [&](int line) {
        InvalidateLuaRenderValues(&values, declared, render_state_context, line);
    };
    const bool trace_render = std::getenv("REFII_LUA_TRACE_RENDER") != nullptr;
    const bool trace_render_lines =
        std::getenv("REFII_LUA_TRACE_RENDER_LINES") != nullptr;
    const auto trace = [&](std::string_view message) {
        if (!trace_render) {
            return;
        }
        std::cerr << "[native-render] " << message << '\n';
    };
    int pending_jump_target = -1;
    std::vector<std::pair<int, int>> loop_target_stack;
    const auto finalize_pending_jump = [&](int current_continue_target,
                                           int current_exit_target) -> bool {
        return FinalizePendingLuaJump(
            &lines, &pending_jump_target, current_continue_target, current_exit_target);
    };

    const auto render_condition = [&](const Lua51Instruction& instruction, int line) {
        return RenderLuaConditionWithValues(values, declaration_map, function, instruction, line);
    };

    const LuaPreviewRenderContext preview_context{
        .instructions = instructions,
        .control_flow_graph = &control_flow_graph,
        .declarations = declaration_map,
        .function = function,
        .register_count = register_count,
        .call_arguments_for = call_arguments_for,
        .render_child = render_child,
        .cache = &preview_cache,
    };

    const auto collect_written_registers_in_range = [&](int start_line, int end_line) {
        if (start_line == end_line) {
            return GetLuaPreviewWrittenRegistersForLine(preview_context, start_line);
        }
        return CollectWrittenLuaRegistersInRange(
            instructions, register_count, start_line, end_line);
    };

    const auto evaluate_inline_range = [&](int start_line,
                                           int end_line,
                                           std::vector<StraightLineValue>* preview_values,
                                           std::string* preview_error) {
        std::vector<bool> preview_declared = declared;
        return EvaluateLuaInlineRange(
            preview_context,
            start_line,
            end_line,
            preview_values,
            &preview_declared,
            preview_error);
    };

    const auto evaluate_branch_path_to_join =
        [&](int start_line,
            int join_line,
            std::vector<StraightLineValue>* preview_values,
            std::string* preview_error) {
            std::vector<bool> preview_declared = declared;
            return EvaluateLuaBranchPathToJoin(
                preview_context,
                start_line,
                join_line,
                preview_values,
                &preview_declared,
                preview_error);
        };

    const auto last_appended_statement_is_terminal =
        [&](std::size_t start_line_count) {
            return LastRenderedStatementIsTerminal(lines, start_line_count);
        };

    const auto preview_range_is_side_effect_free = [&](int start_line, int end_line) {
        return IsLuaPreviewRangeSideEffectFree(preview_context, start_line, end_line);
    };

    std::function<bool(int, int, int, int)> render_range;
    render_range =
        [&](int start_line, int end_line, int loop_continue_target, int loop_exit_target) -> bool {
        if (deadline_exceeded()) {
            return false;
        }
        LuaLoopTargetScope loop_target_scope(
            loop_target_stack, loop_continue_target, loop_exit_target);
        const std::size_t range_base_offset = lines.size();
        if (trace_render) {
            std::ostringstream stream;
            stream << "range start=" << start_line << " end=" << end_line
                   << " continue=" << loop_continue_target << " exit=" << loop_exit_target;
            trace(stream.str());
        }
        const LuaLoopRenderContext loop_render_context{
            .instructions = instructions,
            .declarations = declaration_map,
            .function = function,
            .register_count = register_count,
            .lines = &lines,
            .values = &values,
            .declared = &declared,
            .call_arguments_for = call_arguments_for,
            .return_values_for = return_values_for,
            .setlist_values_for = setlist_values_for,
            .render_child = render_child,
            .resolve_setlist_block = resolve_setlist_block,
            .evaluate_inline_range = evaluate_inline_range,
            .render_condition = render_condition,
            .render_range = render_range,
            .invalidate_values = invalidate_values,
            .finalize_pending_jump = finalize_pending_jump,
            .trace_render = trace_render,
            .trace = trace,
        };
        const LuaLoopRenderResult range_entry_result = TryRenderLuaRangeEntryLoop(
            loop_render_context,
            start_line,
            end_line,
            loop_continue_target,
            loop_exit_target,
            error);
        if (range_entry_result.disposition == LuaLoopRenderDisposition::kError) {
            return false;
        }
        if (range_entry_result.disposition == LuaLoopRenderDisposition::kReturn) {
            return true;
        }

        int current = start_line;
        while (current <= end_line) {
            if (deadline_exceeded()) {
                return false;
            }
            if (current < 1 || static_cast<std::size_t>(current) > instructions.size()) {
                if (error) {
                    *error = "invalid_render_line";
                }
                return false;
            }

            const Lua51Instruction& instruction =
                instructions[static_cast<std::size_t>(current - 1)];
            const int line = static_cast<int>(instruction.line == 0 ? current : instruction.line);
            instruction_output_offsets[static_cast<std::size_t>(current)] = lines.size();
            emit_new_local_declarations(current);
            if (trace_render &&
                (instruction.opcode == Lua51Opcode::kJmp ||
                 IsConditionalOpcode(instruction.opcode) ||
                 instruction.opcode == Lua51Opcode::kForPrep ||
                 instruction.opcode == Lua51Opcode::kTForLoop)) {
                std::ostringstream stream;
                stream << "pc=" << current << " op=" << DescribeLua51Opcode(instruction.opcode)
                       << " continue=" << loop_continue_target << " exit=" << loop_exit_target;
                trace(stream.str());
            }

            if (instruction.opcode == Lua51Opcode::kLoadBool && instruction.c != 0 &&
                !IsLoadBoolPair(instructions, current)) {
                if (error) {
                    *error = "unsupported_loadbool_skip";
                }
                return false;
            }

            const LuaLoopRenderResult loop_result = TryRenderLuaLoopControlFlow(
                loop_render_context,
                current,
                end_line,
                loop_continue_target,
                loop_exit_target,
                line,
                error);
            if (loop_result.disposition == LuaLoopRenderDisposition::kError) {
                return false;
            }
            if (loop_result.disposition == LuaLoopRenderDisposition::kReturn) {
                return true;
            }
            if (loop_result.disposition == LuaLoopRenderDisposition::kContinue) {
                current = loop_result.next_current;
                continue;
            }

            if (IsConditionalOpcode(instruction.opcode)) {
                const LuaBranchRenderContext branch_render_context{
                    .instructions = instructions,
                    .control_flow_graph = control_flow_graph,
                    .preview_context = &preview_context,
                    .declarations = declaration_map,
                    .function = function,
                    .register_count = register_count,
                    .range_start_line = start_line,
                    .lines = &lines,
                    .values = &values,
                    .declared = &declared,
                    .evaluate_inline_range = evaluate_inline_range,
                    .evaluate_branch_path_to_join = evaluate_branch_path_to_join,
                    .preview_range_is_side_effect_free = preview_range_is_side_effect_free,
                    .render_condition = render_condition,
                    .render_range = render_range,
                    .collect_written_registers_in_range = collect_written_registers_in_range,
                    .last_appended_statement_is_terminal = last_appended_statement_is_terminal,
                    .invalidate_values = invalidate_values,
                    .finalize_pending_jump = finalize_pending_jump,
                    .trace_render = trace_render,
                    .trace = trace,
                };
                const LuaJumpRenderResult branch_result = TryRenderLuaConditionalControlFlow(
                    branch_render_context,
                    current,
                    end_line,
                    loop_continue_target,
                    loop_exit_target,
                    line,
                    error);
                if (branch_result.disposition == LuaJumpRenderDisposition::kError) {
                    return false;
                }
                if (branch_result.disposition == LuaJumpRenderDisposition::kReturn) {
                    return true;
                }
                current = branch_result.next_current;
                continue;
            }

            if (instruction.opcode == Lua51Opcode::kJmp) {
                const LuaJumpRenderContext jump_render_context{
                    .instructions = instructions,
                    .instruction_output_offsets = instruction_output_offsets,
                    .control_flow_graph = control_flow_graph,
                    .declarations = declaration_map,
                    .function = function,
                    .register_count = register_count,
                    .range_start_line = start_line,
                    .range_base_offset = range_base_offset,
                    .lines = &lines,
                    .values = &values,
                    .declared = &declared,
                    .call_arguments_for = call_arguments_for,
                    .return_values_for = return_values_for,
                    .evaluate_inline_range = evaluate_inline_range,
                    .render_range = render_range,
                    .render_condition = render_condition,
                    .invalidate_values = invalidate_values,
                    .pending_jump_target = &pending_jump_target,
                    .loop_target_stack = &loop_target_stack,
                    .trace_render = trace_render,
                    .trace = trace,
                };
                const LuaJumpRenderResult jump_result = TryRenderLuaJumpControlFlow(
                    jump_render_context,
                    current,
                    end_line,
                    loop_continue_target,
                    loop_exit_target,
                    line,
                    error);
                if (jump_result.disposition == LuaJumpRenderDisposition::kError) {
                    return false;
                }
                if (jump_result.disposition == LuaJumpRenderDisposition::kReturn) {
                    return true;
                }
                current = jump_result.next_current;
                continue;
            }

            const LuaStatementRenderContext statement_render_context{
                .instructions = instructions,
                .declarations = declaration_map,
                .function = function,
                .register_count = register_count,
                .lines = &lines,
                .values = &values,
                .declared = &declared,
                .call_arguments_for = call_arguments_for,
                .return_values_for = return_values_for,
                .setlist_values_for = setlist_values_for,
                .render_child = render_child,
                .resolve_setlist_block = resolve_setlist_block,
                .trace_render = trace_render,
                .trace = trace,
            };
            const LuaStatementRenderResult statement_result = TryRenderLuaStraightLineStatement(
                statement_render_context,
                start_line,
                end_line,
                current,
                line,
                error);
            if (statement_result.disposition == LuaStatementRenderDisposition::kError) {
                return false;
            }
            if (statement_result.disposition == LuaStatementRenderDisposition::kReturn) {
                return true;
            }
            current = statement_result.next_current;
        }
        return true;
    };

    if (!render_range(1, static_cast<int>(instructions.size()), -1, -1)) {
        return false;
    }
    if (pending_jump_target != -1) {
        if (error) {
            *error = "unresolved_pending_jump@" + std::to_string(pending_jump_target);
        }
        return false;
    }

    if (wrap_function) {
        lines.push_back("end");
    }
    if (trace_render && (trace_render_lines || function.code.size() <= 64)) {
        trace("pre-cleanup-lines-begin");
        for (const std::string& rendered_line : lines) {
            trace(rendered_line);
        }
        trace("pre-cleanup-lines-end");
    }
    const std::vector<std::string> pre_cleanup_lines = lines;
    const bool pre_cleanup_balance_valid = HasValidRenderedLuaBlockBalance(pre_cleanup_lines);
    const bool large_cleanup_body =
        function.code.size() > kLargeCleanupInstructionThreshold ||
        lines.size() > kLargeCleanupLineThreshold;
    const LuaScriptPolicy cleanup_policy =
        BuildLuaScriptPolicy(function, static_cast<int>(instructions.size()));
    const bool runtime_light_hotspot =
        cleanup_policy.profile.Has(LuaScriptProfileFlag::kRuntimeLightModeHotspot);
    const bool disable_simplify =
        !simplify_rendered_output ||
        std::getenv("REFII_LUA_DISABLE_RENDER_SIMPLIFY") != nullptr;
    const int max_profile_passes =
        disable_simplify ? 1 : (runtime_light_hotspot || large_cleanup_body ? 2 : 4);
    RunBoundedRenderPassLoop(max_profile_passes, [&](int pass) {
        if (deadline_exceeded()) {
            return false;
        }
        bool changed = false;
        const auto step = [&](std::string_view name) {
            if (!trace_render) {
                return;
            }
            std::cerr << "[native-render] cleanup pass=" << pass << " step=" << name << '\n';
        };
        step("ScanRenderedLuaCleanupFeatures");
        const LuaRenderedCleanupFeatures features = ScanRenderedLuaCleanupFeatures(lines);
        if (!large_cleanup_body || features.has_table_builder_candidates) {
            step("NormalizeRenderedLuaLoopActionBuilderAliases");
            changed |= NormalizeRenderedLuaLoopActionBuilderAliases(&lines);
            step("NormalizeRenderedLuaNilTablePlaceholders");
            changed |= NormalizeRenderedLuaNilTablePlaceholders(&lines);
        }
        if (!large_cleanup_body || features.has_temporary_candidates) {
            step("SimplifyRenderedLuaTemporaries");
            changed |= SimplifyRenderedLuaTemporaries(&lines);
            step("SimplifyRenderedLuaReturnTemporaries");
            changed |= SimplifyRenderedLuaReturnTemporaries(&lines);
            step("LocalizeRenderedLuaFirstAssignments");
            changed |= LocalizeRenderedLuaFirstAssignments(&lines);
            step("LocalizeRenderedLuaFunctionLocals");
            changed |= LocalizeRenderedLuaFunctionLocals(&lines);
        }
        if (!large_cleanup_body || features.has_conditional_iife_candidates) {
            step("SimplifyRenderedLuaConditionalIifes");
            changed |= SimplifyRenderedLuaConditionalIifes(&lines);
        }
        if (!large_cleanup_body || features.has_call_parenthesis_candidates) {
            step("SimplifyRenderedLuaCallParentheses");
            changed |= SimplifyRenderedLuaCallParentheses(&lines);
        }
        step("DisambiguateRenderedLuaLeadingIifeStatements");
        changed |= DisambiguateRenderedLuaLeadingIifeStatements(&lines);
        step("ApplySharedRenderedLuaCleanup");
        changed |= internal::ApplySharedRenderedLuaCleanup(
            &lines,
            disable_simplify
                ? internal::RenderedLuaCleanupProfile::kValidatedOutput
                : internal::RenderedLuaCleanupProfile::kAggressiveCandidateRefinement);
        step("RepairRenderedLuaSourceGuardedTemporaryFallbacks");
        changed |= RepairRenderedLuaSourceGuardedTemporaryFallbacks(&lines);
        step("NormalizeRenderedLuaWhitespace");
        changed |= NormalizeRenderedLuaWhitespace(&lines);
        step("pass-end");
        if (!changed) {
            return false;
        }
        return true;
    });
    RepairRenderedLuaSourceGuardedTemporaryFallbacks(&lines);
    NormalizeRenderedLuaWhitespace(&lines);
    if (pre_cleanup_balance_valid && !HasValidRenderedLuaBlockBalance(lines)) {
        if (trace_render) {
            trace("cleanup_invalid_block_balance_reverted_to_pre_cleanup");
        }
        lines = pre_cleanup_lines;
    }
    if (trace_render && (trace_render_lines || function.code.size() <= 64)) {
        trace("post-cleanup-lines-begin");
        for (const std::string& rendered_line : lines) {
            trace(rendered_line);
        }
        trace("post-cleanup-lines-end");
    }
    std::ostringstream stream;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (i != 0) {
            stream << '\n';
        }
        stream << lines[i];
    }
    *output = stream.str();
    return true;
}

bool RenderLua51StraightLineFunction(const Lua51Function& function,
                                     std::string* output,
                                     std::string* error) {
    std::string local_error;
    const bool success =
        RenderLua51StraightLineFunctionImpl(function, true, true, true, output, &local_error);
    if (!success && error != nullptr) {
        *error = std::move(local_error);
    }
    return success;
}

bool RenderLua51StraightLineFunctionRaw(const Lua51Function& function,
                                        std::string* output,
                                        std::string* error) {
    std::string local_error;
    const bool success =
        RenderLua51StraightLineFunctionImpl(function, true, false, true, output, &local_error);
    if (!success && error != nullptr) {
        *error = std::move(local_error);
    }
    return success;
}

bool RenderLua51StraightLineFunctionBody(const Lua51Function& function,
                                         std::string* output,
                                         std::string* error) {
    std::string local_error;
    const bool success =
        RenderLua51StraightLineFunctionImpl(function, false, true, true, output, &local_error);
    if (!success && error != nullptr) {
        *error = std::move(local_error);
    }
    return success;
}

bool RenderLua51StraightLineFunctionBodyRaw(const Lua51Function& function,
                                            std::string* output,
                                            std::string* error) {
    std::string local_error;
    const bool success =
        RenderLua51StraightLineFunctionImpl(function, false, false, true, output, &local_error);
    if (!success && error != nullptr) {
        *error = std::move(local_error);
    }
    return success;
}

}  // namespace refii::lua
