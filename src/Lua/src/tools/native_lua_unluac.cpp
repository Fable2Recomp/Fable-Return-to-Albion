#include "tools/native_lua_unluac.h"
#include "tools/decompiler/branch_render.h"
#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/control_flow_graph.h"
#include "tools/decompiler/jump_render.h"
#include "tools/decompiler/loop_render.h"
#include "tools/decompiler/preview_render.h"
#include "tools/decompiler/render_state.h"
#include "tools/decompiler/rendered_lua_text.h"
#include "tools/decompiler/statement_render.h"
#include "tools/decompiler/straight_line_render.h"

#include <algorithm>
#include <cstdlib>
#include <functional>
#include <iostream>
#include <sstream>
#include <utility>

namespace refii::lua {
namespace {

constexpr int kLua51MaxStack = 250;
constexpr std::size_t kLargeCleanupInstructionThreshold = 12000;
constexpr std::size_t kLargeCleanupLineThreshold = 4000;

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

}  // namespace

bool RenderLua51StraightLineFunctionImpl(const Lua51Function& function,
                                         bool wrap_function,
                                         bool simplify_rendered_output,
                                         std::string* output,
                                         std::string* error) {
    if (output == nullptr) {
        if (error) {
            *error = "render_output_missing";
        }
        return false;
    }

    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    const decompiler::Lua51ControlFlowGraph control_flow_graph =
        decompiler::BuildLua51ControlFlowGraph(instructions);
    for (std::size_t i = 0; i < instructions.size(); ++i) {
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

    const auto setlist_values_for = [&](const std::vector<StraightLineValue>& state_values,
                                        int a,
                                        int b,
                                        int line) {
        std::vector<StraightLineValue> entries;
        if (b == 0) {
            for (int reg = a + 1; reg < register_count; ++reg) {
                const StraightLineValue value =
                    GetRegisterValue(state_values, declaration_map, function, reg, line);
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
            entries.push_back(
                GetRegisterValue(state_values, declaration_map, function, reg, line));
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

    auto render_child = [&](int child_index, std::string* rendered_child) {
        if (child_index < 0 ||
            static_cast<std::size_t>(child_index) >= function.child_functions.size()) {
            return false;
        }
        return RenderLua51StraightLineFunctionImpl(
            function.child_functions[static_cast<std::size_t>(child_index)],
            true,
            simplify_rendered_output,
            rendered_child,
            error);
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

    const auto collect_written_registers_in_range = [&](int start_line, int end_line) {
        return CollectWrittenLuaRegistersInRange(
            instructions, register_count, start_line, end_line);
    };
    const bool trace_render = std::getenv("REFII_LUA_TRACE_RENDER") != nullptr;
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
        .declarations = declaration_map,
        .function = function,
        .register_count = register_count,
        .call_arguments_for = call_arguments_for,
        .render_child = render_child,
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
    const bool large_cleanup_body =
        function.code.size() > kLargeCleanupInstructionThreshold ||
        lines.size() > kLargeCleanupLineThreshold;
    const bool disable_simplify =
        !simplify_rendered_output ||
        std::getenv("REFII_LUA_DISABLE_RENDER_SIMPLIFY") != nullptr;
    if (disable_simplify) {
        const int max_passes = large_cleanup_body ? 1 : 3;
        for (int pass = 0; pass < max_passes; ++pass) {
            bool changed = false;
            const LuaRenderedCleanupFeatures features =
                ScanRenderedLuaCleanupFeatures(lines);
            changed |= RemoveRenderedLuaNoOpAssignments(&lines);
            if (!large_cleanup_body || features.has_temporary_candidates) {
                changed |= SimplifyRenderedLuaAssignedFunctionTemporaries(&lines);
                changed |= SimplifyRenderedLuaFunctionTemporaries(&lines);
                changed |= SimplifyRenderedLuaReturnTemporaries(&lines);
            }
            if (!large_cleanup_body || features.has_conditional_iife_candidates) {
                changed |= SimplifyRenderedLuaConditionalIifes(&lines);
            }
            changed |= SimplifyRenderedLuaConditionHeaders(&lines);
            changed |= SimplifyRenderedLuaExpressionParentheses(&lines);
            if (!large_cleanup_body || features.has_call_parenthesis_candidates) {
                changed |= SimplifyRenderedLuaCallParentheses(&lines);
            }
            changed |= DisambiguateRenderedLuaLeadingIifeStatements(&lines);
            if (!changed) {
                break;
            }
        }
    } else {
        const int max_passes = large_cleanup_body ? 6 : 16;
        for (int pass = 0; pass < max_passes; ++pass) {
            bool changed = false;
            const LuaRenderedCleanupFeatures features =
                ScanRenderedLuaCleanupFeatures(lines);
            changed |= RemoveRenderedLuaEmptyIfBlocks(&lines);
            changed |= RemoveRenderedLuaNoOpAssignments(&lines);
            if (!large_cleanup_body || features.has_temporary_candidates) {
                changed |= SimplifyRenderedLuaAssignedFunctionTemporaries(&lines);
                changed |= SimplifyRenderedLuaFunctionTemporaries(&lines);
                changed |= SimplifyRenderedLuaTemporaries(&lines);
                changed |= SimplifyRenderedLuaReturnTemporaries(&lines);
                changed |= LocalizeRenderedLuaFirstAssignments(&lines);
                changed |= LocalizeRenderedLuaFunctionLocals(&lines);
            }
            changed |= SimplifyRenderedLuaDotAccess(&lines);
            if (!large_cleanup_body || features.has_table_builder_candidates) {
                changed |= SimplifyRenderedLuaTableBuilders(&lines);
                changed |= SimplifyRenderedLuaReturnedTableBuilders(&lines);
                changed |= SimplifyRenderedLuaTableInitializerTemporaries(&lines);
                changed |= SimplifyRenderedLuaTableLiteralTemporaries(&lines);
            }
            changed |= RemoveRenderedLuaNoOpAssignments(&lines);
            if (!large_cleanup_body || features.has_temporary_candidates) {
                changed |= SimplifyRenderedLuaTemporaries(&lines);
                changed |= SimplifyRenderedLuaReturnTemporaries(&lines);
            }
            if (!large_cleanup_body || features.has_conditional_iife_candidates) {
                changed |= SimplifyRenderedLuaConditionalIifes(&lines);
            }
            changed |= SimplifyRenderedLuaConditionHeaders(&lines);
            changed |= SimplifyRenderedLuaExpressionParentheses(&lines);
            if (!large_cleanup_body || features.has_temporary_candidates) {
                changed |= LocalizeRenderedLuaFirstAssignments(&lines);
                changed |= LocalizeRenderedLuaFunctionLocals(&lines);
            }
            if (!large_cleanup_body || features.has_call_parenthesis_candidates) {
                changed |= SimplifyRenderedLuaCallParentheses(&lines);
            }
            changed |= DisambiguateRenderedLuaLeadingIifeStatements(&lines);
            if (!changed) {
                break;
            }
        }
    }
    NormalizeRenderedLuaWhitespace(&lines);
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
        RenderLua51StraightLineFunctionImpl(function, true, true, output, &local_error);
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
        RenderLua51StraightLineFunctionImpl(function, true, false, output, &local_error);
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
        RenderLua51StraightLineFunctionImpl(function, false, true, output, &local_error);
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
        RenderLua51StraightLineFunctionImpl(function, false, false, output, &local_error);
    if (!success && error != nullptr) {
        *error = std::move(local_error);
    }
    return success;
}

}  // namespace refii::lua
