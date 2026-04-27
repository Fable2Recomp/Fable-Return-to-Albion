#include "tools/decompiler/preview_fallback_render.h"

#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/preview_operation_render.h"
#include "tools/decompiler/preview_join_render.h"
#include "tools/decompiler/preview_render.h"
#include "tools/decompiler/render_state.h"

#include <algorithm>
#include <chrono>
#include <cctype>
#include <string>

namespace refii::lua {
namespace {

constexpr int kLuaMaxFallbackBranchDepth = 16;
constexpr int kLuaMaxFallbackBranchSpan = 96;
constexpr int kLuaMaxFallbackBranchNodesDefault = 512;
constexpr int kLuaMaxFallbackBranchNodesQuest = 192;
constexpr int kLuaMaxFallbackBranchNodesQc010 = 64;

bool IsLuaFallbackQuestSource(const Lua51Function& function) {
    const LuaScriptProfile profile = ClassifyLuaScriptProfile(function);
    return profile.Has(LuaScriptProfileFlag::kFallbackQuestHotspot);
}

bool IsLuaFallbackQc010Source(const Lua51Function& function) {
    const LuaScriptProfile profile = ClassifyLuaScriptProfile(function);
    return profile.Has(LuaScriptProfileFlag::kFallbackQc010);
}

int GetLuaFallbackBranchSpanBudget(const LuaPreviewFallbackContext& context) {
    if (IsLuaFallbackQc010Source(context.function)) {
        return 48;
    }
    if (IsLuaFallbackQuestSource(context.function)) {
        return 64;
    }
    return kLuaMaxFallbackBranchSpan;
}

int GetLuaFallbackNodeBudget(const LuaPreviewFallbackContext& context) {
    if (IsLuaFallbackQc010Source(context.function)) {
        return kLuaMaxFallbackBranchNodesQc010;
    }
    if (IsLuaFallbackQuestSource(context.function)) {
        return kLuaMaxFallbackBranchNodesQuest;
    }
    return kLuaMaxFallbackBranchNodesDefault;
}

std::chrono::steady_clock::time_point GetLuaFallbackEvaluationDeadline(
    const LuaPreviewFallbackContext& context) {
    const auto now = std::chrono::steady_clock::now();
    if (IsLuaFallbackQc010Source(context.function)) {
        return now + std::chrono::milliseconds(20);
    }
    if (IsLuaFallbackQuestSource(context.function)) {
        return now + std::chrono::milliseconds(40);
    }
    return now + std::chrono::milliseconds(120);
}

bool IsLuaFallbackFunctionBoundaryReturnLine(const std::vector<Lua51Instruction>& instructions,
                                             int line,
                                             int target_register) {
    if (line < 1 || static_cast<std::size_t>(line) > instructions.size()) {
        return false;
    }
    const Lua51Instruction& instruction =
        instructions[static_cast<std::size_t>(line - 1)];
    if (instruction.opcode != Lua51Opcode::kReturn) {
        return false;
    }
    if (instruction.b == 1) {
        return false;
    }
    if (target_register < 0) {
        return true;
    }
    if (instruction.b == 0) {
        return target_register >= instruction.a;
    }
    return target_register >= instruction.a &&
           target_register < instruction.a + instruction.b - 1;
}

bool IsLuaFallbackFunctionBoundaryConditionalReturnShape(
    const Lua51Function& function,
    const std::vector<Lua51Instruction>& instructions,
    int branch_target,
    int join_line,
    int target_register) {
    if (instructions.empty()) {
        return false;
    }
    constexpr std::size_t kMaxFunctionBoundaryParityInstructionCount = 1200;
    constexpr std::size_t kQuestBoundaryParityInstructionCount = 3200;
    const std::size_t max_instruction_count =
        IsLuaFallbackQuestSource(function)
            ? kQuestBoundaryParityInstructionCount
            : kMaxFunctionBoundaryParityInstructionCount;
    if (instructions.size() > max_instruction_count) {
        return false;
    }
    const int instruction_count = static_cast<int>(instructions.size());
    if (branch_target == instruction_count + 1 ||
        join_line == instruction_count + 1) {
        return true;
    }
    return IsLuaFallbackFunctionBoundaryReturnLine(instructions, branch_target, target_register) ||
           IsLuaFallbackFunctionBoundaryReturnLine(instructions, join_line, target_register) ||
           IsLuaFallbackFunctionBoundaryReturnLine(instructions, branch_target + 1, target_register) ||
           IsLuaFallbackFunctionBoundaryReturnLine(instructions, join_line + 1, target_register);
}

bool EvaluateLuaPreviewBranchFallbackPath(const LuaPreviewFallbackContext& context,
                                          int start_line,
                                          int join_line,
                                          std::vector<StraightLineValue>* preview_values,
                                          std::vector<bool>* preview_declared,
                                          std::string* preview_error,
                                          int depth,
                                          int* remaining_nodes,
                                          const std::chrono::steady_clock::time_point* deadline) {
    if (context.preview_context == nullptr || preview_values == nullptr ||
        preview_declared == nullptr || start_line < 1 || join_line < start_line ||
        static_cast<std::size_t>(join_line) > context.preview_context->instructions.size() ||
        depth > kLuaMaxFallbackBranchDepth || remaining_nodes == nullptr ||
        deadline == nullptr) {
        if (preview_error != nullptr && depth > kLuaMaxFallbackBranchDepth) {
            *preview_error = "fallback_branch_depth";
        }
        return false;
    }
    if (std::chrono::steady_clock::now() >= *deadline) {
        if (preview_error != nullptr) {
            *preview_error = "fallback_branch_deadline";
        }
        return false;
    }
    if (*remaining_nodes <= 0) {
        if (preview_error != nullptr) {
            *preview_error = "fallback_branch_budget";
        }
        return false;
    }
    --(*remaining_nodes);

    const int span_budget = GetLuaFallbackBranchSpanBudget(context);
    if (join_line - start_line > span_budget) {
        if (preview_error != nullptr) {
            *preview_error = "fallback_branch_span";
        }
        return false;
    }

    int current = start_line;
    while (current < join_line) {
        if (current < 1 ||
            static_cast<std::size_t>(current) > context.preview_context->instructions.size()) {
            if (preview_error != nullptr) {
                *preview_error = "fallback_branch_line";
            }
            return false;
        }

        const Lua51Instruction& instruction =
            context.preview_context->instructions[static_cast<std::size_t>(current - 1)];
        const int line = static_cast<int>(instruction.line == 0 ? current : instruction.line);
        if (decompiler::IsConditionalBranchOpcode(instruction.opcode)) {
            if (join_line - current > span_budget) {
                if (preview_error != nullptr) {
                    *preview_error = "fallback_branch_span";
                }
                return false;
            }
            LuaConditionalRootCommitPacket root_packet;
            if (!TryResolveLuaPreviewConditionalRootCommitPacket(*context.preview_context,
                                                                 current,
                                                                 join_line - 1,
                                                                 join_line,
                                                                 -1,
                                                                 join_line,
                                                                 join_line,
                                                                 &root_packet) ||
                !root_packet.chain_proven ||
                !root_packet.ownership_proven) {
                if (preview_error != nullptr) {
                    *preview_error = "fallback_branch_shape";
                }
                return false;
            }
            const LuaConditionalChainSpan& chain_span = root_packet.chain_span;
            const int branch_target =
                root_packet.branch_target_line > 0 ? root_packet.branch_target_line : join_line;
            const int ownership_target_register = root_packet.carried_register;

            const int jump_target = chain_span.join_line;
            int fallthrough_start = current + 2;
            if (chain_span.links.size() > 1) {
                bool chain_contains_carried_testset = false;
                for (const LuaConditionalChainLink& chain_entry : chain_span.links) {
                    if (chain_entry.opcode == Lua51Opcode::kTestSet &&
                        chain_entry.target_register >= 0 &&
                        chain_entry.source_register >= 0 &&
                        chain_entry.target_register != chain_entry.source_register) {
                        chain_contains_carried_testset = true;
                        break;
                    }
                }
                if (!chain_contains_carried_testset) {
                    fallthrough_start = chain_span.next_line;
                }
            }
            if (jump_target < fallthrough_start || jump_target > join_line) {
                if (preview_error != nullptr) {
                    *preview_error = "fallback_branch_target";
                }
                return false;
            }

            const LuaBranchStateSnapshot base_state{*preview_values, *preview_declared};

            LuaBranchStateSnapshot jump_state = base_state;
            std::vector<bool> jump_written_registers =
                jump_target < join_line
                    ? CollectWrittenLuaRegistersInRange(context.preview_context->instructions,
                                                        context.register_count,
                                                        jump_target,
                                                        join_line - 1)
                    : std::vector<bool>(static_cast<std::size_t>(context.register_count), false);
            ApplyLuaConditionalChainTestSetCarryEffects(
                context.preview_context->instructions,
                chain_span,
                context.declarations,
                context.function,
                &jump_state.values,
                ownership_target_register >= 0 ? ownership_target_register : -1,
                true);
            for (const LuaConditionalChainLink& chain_entry : chain_span.links) {
                if (chain_entry.opcode != Lua51Opcode::kTestSet ||
                    chain_entry.target_register < 0 ||
                    static_cast<std::size_t>(chain_entry.target_register) >=
                        jump_state.values.size()) {
                    continue;
                }
                if (ownership_target_register >= 0 &&
                    chain_entry.target_register != ownership_target_register) {
                    continue;
                }
                if (chain_entry.target_register >= context.function.parameter_count &&
                    static_cast<std::size_t>(chain_entry.target_register) <
                        jump_state.declared.size()) {
                    jump_state.declared[static_cast<std::size_t>(chain_entry.target_register)] = true;
                }
                if (static_cast<std::size_t>(chain_entry.target_register) <
                    jump_written_registers.size()) {
                    jump_written_registers[static_cast<std::size_t>(chain_entry.target_register)] =
                        true;
                }
            }
            if (jump_target < join_line &&
                !EvaluateLuaPreviewBranchFallbackPath(context,
                                                     jump_target,
                                                     join_line,
                                                     &jump_state.values,
                                                     &jump_state.declared,
                                                     preview_error,
                                                     depth + 1,
                                                     remaining_nodes,
                                                     deadline)) {
                return false;
            }

            LuaBranchStateSnapshot fallthrough_state = base_state;
            if (!EvaluateLuaPreviewBranchFallbackPath(context,
                                                     fallthrough_start,
                                                     join_line,
                                                     &fallthrough_state.values,
                                                     &fallthrough_state.declared,
                                                     preview_error,
                                                     depth + 1,
                                                     remaining_nodes,
                                                     deadline)) {
                return false;
            }

            const std::vector<bool> fallthrough_written_registers =
                CollectWrittenLuaRegistersInRange(context.preview_context->instructions,
                                                 context.register_count,
                                                 fallthrough_start,
                                                 join_line - 1);
            FinalizeLuaJoinCandidateState(&jump_state.values,
                                          &jump_state.declared,
                                          base_state.values,
                                          base_state.declared,
                                          jump_written_registers,
                                          context.declarations,
                                          context.function,
                                          join_line,
                                          context.register_count);
            FinalizeLuaJoinCandidateState(&fallthrough_state.values,
                                          &fallthrough_state.declared,
                                          base_state.values,
                                          base_state.declared,
                                          fallthrough_written_registers,
                                          context.declarations,
                                          context.function,
                                          join_line,
                                          context.register_count);

            const bool prefer_function_boundary_parity =
                IsLuaFallbackFunctionBoundaryConditionalReturnShape(
                    context.function,
                    context.preview_context->instructions,
                    branch_target,
                    chain_span.join_line,
                    ownership_target_register);
            const std::string condition =
                prefer_function_boundary_parity
                    ? RenderLuaConditionWithFunctionBoundaryParity(base_state.values,
                                                                   context.declarations,
                                                                   context.function,
                                                                   instruction,
                                                                   line)
                    : RenderLuaConditionWithValues(base_state.values,
                                                   context.declarations,
                                                   context.function,
                                                   instruction,
                                                   line);
            int changed_register = -1;
            StraightLineValue merged_value;
            if (TryBuildLuaPreviewSingleRegisterJoinExpression(condition,
                                                               fallthrough_state.values,
                                                               fallthrough_state.declared,
                                                               fallthrough_written_registers,
                                                               jump_state.values,
                                                               jump_state.declared,
                                                               jump_written_registers,
                                                               context.declarations,
                                                               context.function,
                                                               join_line,
                                                               context.register_count,
                                                               &changed_register,
                                                               &merged_value)) {
                *preview_values = fallthrough_state.values;
                *preview_declared = fallthrough_state.declared;
                (*preview_values)[static_cast<std::size_t>(changed_register)] =
                    std::move(merged_value);
                return true;
            }

            const LuaMergedBranchState merged_state =
                BuildLuaMergedBranchState(base_state,
                                          jump_state,
                                          jump_written_registers,
                                          fallthrough_state,
                                          fallthrough_written_registers,
                                          context.declarations,
                                          context.function,
                                          join_line,
                                          context.register_count);
            *preview_values = merged_state.values;
            *preview_declared = merged_state.declared;
            return true;
        }

        if (instruction.opcode == Lua51Opcode::kJmp) {
            const int jump_target = decompiler::ComputeJumpTargetLine(
                current,
                instruction.sbx,
                static_cast<int>(context.preview_context->instructions.size()));
            if (jump_target < current + 1 || jump_target > join_line) {
                if (preview_error != nullptr) {
                    *preview_error = "fallback_jump_target";
                }
                return false;
            }
            current = jump_target;
            continue;
        }

        if (instruction.opcode == Lua51Opcode::kClose) {
            ++current;
            continue;
        }

        const LuaPreviewOperationContext preview_context{
            .instructions = context.preview_context->instructions,
            .declarations = context.declarations,
            .function = context.function,
            .register_count = context.register_count,
            .values = preview_values,
            .declared = preview_declared,
            .call_arguments_for = context.preview_context->call_arguments_for,
            .render_child = context.preview_context->render_child,
        };
        LuaPreviewOperationDisposition disposition =
            LuaPreviewOperationDisposition::kBuildFailure;
        std::string build_error;
        int next_current = current + 1;
        if (!TryApplyLuaPreviewOperation(preview_context,
                                         current,
                                         line,
                                         LuaPreviewOperationMode::kStateTracking,
                                         &disposition,
                                         &build_error,
                                         &next_current)) {
            if (preview_error != nullptr) {
                *preview_error = build_error.empty() ? "fallback_preview_opcode"
                                                     : build_error;
            }
            return false;
        }
        current = next_current;
    }

    return current == join_line;
}

}  // namespace

bool TryMergeLuaPreviewRangeFallback(const LuaPreviewFallbackContext& context,
                                     int fallback_start,
                                     int fallback_end,
                                     int line,
                                     const std::vector<StraightLineValue>& values,
                                     int preferred_reg,
                                     bool require_temporary_declaration,
                                     StraightLineValue* merged_value,
                                     const StraightLineValue* initial_target_value) {
    if (merged_value == nullptr || !context.evaluate_inline_range ||
        fallback_start > fallback_end) {
        return false;
    }

    std::vector<StraightLineValue> original_values_for_merge = values;
    if (initial_target_value != nullptr && preferred_reg >= 0 &&
        static_cast<std::size_t>(preferred_reg) < original_values_for_merge.size()) {
        original_values_for_merge[static_cast<std::size_t>(preferred_reg)] =
            *initial_target_value;
    }

    std::vector<StraightLineValue> fallback_values = original_values_for_merge;
    std::vector<bool> original_declared_for_merge;
    std::vector<bool> fallback_declared;
    std::string preview_error;
    if (context.preview_context != nullptr && context.initial_declared != nullptr) {
        original_declared_for_merge = *context.initial_declared;
        fallback_declared = original_declared_for_merge;
        if (!EvaluateLuaInlineRange(*context.preview_context,
                                    fallback_start,
                                    fallback_end,
                                    &fallback_values,
                                    &fallback_declared,
                                    &preview_error)) {
            return false;
        }

        const std::vector<bool> written_registers = CollectWrittenLuaRegistersInRange(
            context.preview_context->instructions,
            context.register_count,
            fallback_start,
            fallback_end);
        FinalizeLuaJoinCandidateState(&fallback_values,
                                      &fallback_declared,
                                      original_values_for_merge,
                                      original_declared_for_merge,
                                      written_registers,
                                      context.declarations,
                                      context.function,
                                      line,
                                      context.register_count);
    } else {
        if (!context.evaluate_inline_range(
                fallback_start, fallback_end, &fallback_values, &preview_error)) {
            return false;
        }
    }

    return TryMergeLuaPreviewRegisterFallback(original_values_for_merge,
                                              original_declared_for_merge.empty()
                                                  ? nullptr
                                                  : &original_declared_for_merge,
                                              fallback_values,
                                              fallback_declared.empty()
                                                  ? nullptr
                                                  : &fallback_declared,
                                              context.declarations,
                                              context.function,
                                              line,
                                              context.register_count,
                                              preferred_reg,
                                              require_temporary_declaration,
                                              merged_value);
}

bool TryMergeLuaPreviewBranchPathFallback(const LuaPreviewFallbackContext& context,
                                          int fallback_start,
                                          int join_line,
                                          int line,
                                          const std::vector<StraightLineValue>& values,
                                          int preferred_reg,
                                          bool require_temporary_declaration,
                                          StraightLineValue* merged_value,
                                          const StraightLineValue* initial_target_value) {
    if (merged_value == nullptr || context.preview_context == nullptr ||
        context.initial_declared == nullptr || fallback_start >= join_line) {
        return false;
    }

    std::vector<StraightLineValue> original_values_for_merge = values;
    if (initial_target_value != nullptr && preferred_reg >= 0 &&
        static_cast<std::size_t>(preferred_reg) < original_values_for_merge.size()) {
        original_values_for_merge[static_cast<std::size_t>(preferred_reg)] =
            *initial_target_value;
    }

    std::vector<StraightLineValue> fallback_values = original_values_for_merge;
    std::vector<bool> original_declared_for_merge = *context.initial_declared;
    std::vector<bool> fallback_declared = original_declared_for_merge;
    std::string preview_error;
    int remaining_nodes = GetLuaFallbackNodeBudget(context);
    const auto deadline = GetLuaFallbackEvaluationDeadline(context);
    if (!EvaluateLuaPreviewBranchFallbackPath(context,
                                              fallback_start,
                                              join_line,
                                              &fallback_values,
                                              &fallback_declared,
                                              &preview_error,
                                              0,
                                              &remaining_nodes,
                                              &deadline)) {
        return false;
    }

    const std::vector<bool> written_registers = CollectWrittenLuaRegistersInRange(
        context.preview_context->instructions,
        context.register_count,
        fallback_start,
        join_line - 1);
    FinalizeLuaJoinCandidateState(&fallback_values,
                                  &fallback_declared,
                                  original_values_for_merge,
                                  original_declared_for_merge,
                                  written_registers,
                                  context.declarations,
                                  context.function,
                                  line,
                                  context.register_count);

    return TryMergeLuaPreviewRegisterFallback(original_values_for_merge,
                                              &original_declared_for_merge,
                                              fallback_values,
                                              &fallback_declared,
                                              context.declarations,
                                              context.function,
                                              line,
                                              context.register_count,
                                              preferred_reg,
                                              require_temporary_declaration,
                                              merged_value);
}

bool TryEvaluateLuaPreviewBranchPathValue(const LuaPreviewFallbackContext& context,
                                          int start_line,
                                          int join_line,
                                          int line,
                                          const std::vector<StraightLineValue>& values,
                                          int target_reg,
                                          StraightLineValue* value,
                                          const StraightLineValue* initial_target_value) {
    if (value == nullptr || context.preview_context == nullptr ||
        context.initial_declared == nullptr || target_reg < 0 ||
        static_cast<std::size_t>(target_reg) >= values.size() ||
        start_line < 1 || join_line < start_line) {
        return false;
    }

    std::vector<StraightLineValue> preview_values = values;
    if (initial_target_value != nullptr &&
        static_cast<std::size_t>(target_reg) < preview_values.size()) {
        preview_values[static_cast<std::size_t>(target_reg)] = *initial_target_value;
    }
    std::vector<bool> preview_declared = *context.initial_declared;
    std::string preview_error;
    int remaining_nodes = GetLuaFallbackNodeBudget(context);
    const auto deadline = GetLuaFallbackEvaluationDeadline(context);
    if (!EvaluateLuaPreviewBranchFallbackPath(context,
                                              start_line,
                                              join_line,
                                              &preview_values,
                                              &preview_declared,
                                              &preview_error,
                                              0,
                                              &remaining_nodes,
                                              &deadline)) {
        return false;
    }

    const StraightLineValue resolved_value = GetRegisterValue(preview_values,
                                                              context.declarations,
                                                              context.function,
                                                              target_reg,
                                                              line);
    if (!resolved_value.valid) {
        return false;
    }

    *value = resolved_value;
    return true;
}

}  // namespace refii::lua
