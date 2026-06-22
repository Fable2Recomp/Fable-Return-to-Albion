#include "tools/decompiler/branch_preview_render.h"

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/preview_fallback_render.h"
#include "tools/decompiler/preview_join_render.h"
#include "tools/decompiler/preview_operation_render.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/straight_line_render.h"
#include "tools/decompiler/testset_render.h"

#include <algorithm>
#include <cctype>
#include <string>

namespace refii::lua {
namespace {

thread_local std::size_t g_lua_native_statementtemp_commits = 0;
thread_local std::size_t g_lua_native_callvalue_ownership_commits = 0;

const LuaScriptPolicy& GetLuaBranchPreviewPolicy(
    const LuaPreviewRenderContext& preview_context) {
    static thread_local const Lua51Function* last_function = nullptr;
    static thread_local std::size_t last_instruction_count = 0;
    static thread_local LuaScriptPolicy last_policy;
    if (&preview_context.function == last_function &&
        preview_context.instructions.size() == last_instruction_count) {
        return last_policy;
    }
    last_policy = BuildLuaScriptPolicy(
        preview_context.function,
        static_cast<int>(preview_context.instructions.size()));
    last_function = &preview_context.function;
    last_instruction_count = preview_context.instructions.size();
    return last_policy;
}

bool IsLuaBranchPreviewQuestBoundaryParityFunction(const Lua51Function& function) {
    const LuaScriptProfile& profile = GetLuaScriptProfileCached(function);
    return profile.Has(LuaScriptProfileFlag::kQuestHotspot);
}

bool IsLuaBranchPreviewFastPathEnabled(
    const LuaPreviewRenderContext& preview_context) {
    const LuaScriptPolicy policy = GetLuaBranchPreviewPolicy(preview_context);
    return IsLuaPolicyPreviewFastPathEnabled(policy, preview_context.instructions.size());
}

bool IsLuaBranchPreviewCheapConditionalRootShape(
    const LuaPreviewRenderContext& preview_context,
    int condition_line,
    int join_line) {
    if (condition_line < 1 ||
        static_cast<std::size_t>(condition_line + 1) > preview_context.instructions.size()) {
        return false;
    }
    const Lua51Instruction& root =
        preview_context.instructions[static_cast<std::size_t>(condition_line - 1)];
    if (!decompiler::IsConditionalBranchOpcode(root.opcode)) {
        return false;
    }
    const Lua51Instruction& jump =
        preview_context.instructions[static_cast<std::size_t>(condition_line)];
    if (jump.opcode != Lua51Opcode::kJmp) {
        return false;
    }
    const auto is_supported_root_opcode = [&](Lua51Opcode opcode) {
        return opcode == Lua51Opcode::kTest ||
               opcode == Lua51Opcode::kTestSet ||
               opcode == Lua51Opcode::kEq ||
               opcode == Lua51Opcode::kLt ||
               opcode == Lua51Opcode::kLe;
    };
    if (!is_supported_root_opcode(root.opcode)) {
        return false;
    }
    if (join_line <= condition_line) {
        return false;
    }
    const LuaScriptPolicy policy = GetLuaBranchPreviewPolicy(preview_context);
    const int max_span = policy.preview_root_shape_span;
    return join_line - condition_line <= max_span;
}

bool TryResolveLuaBranchPreviewRootPacket(const LuaPreviewRenderContext& preview_context,
                                          int condition_line,
                                          int branch_target,
                                          int join_line,
                                          LuaConditionalRootCommitPacket* packet_out) {
    if (packet_out == nullptr || condition_line < 1 || branch_target < condition_line ||
        join_line < branch_target) {
        return false;
    }
    return TryResolveLuaPreviewConditionalRootCommitPacket(preview_context,
                                                           condition_line,
                                                           std::max(condition_line,
                                                                    branch_target - 2),
                                                           std::max(branch_target, join_line),
                                                           branch_target,
                                                           branch_target,
                                                           join_line,
                                                           packet_out);
}

bool IsLuaBranchPreviewJoinLineCompatibleWithOwnershipCommit(
    const LuaPreviewRenderContext& preview_context,
    const LuaJoinOwnershipCommitDecision& commit,
    int join_line) {
    if (!commit.proven || commit.effective_join_line <= 0 || join_line <= 0) {
        return false;
    }
    if (commit.effective_join_line == join_line) {
        return true;
    }

    const int lower = std::min(commit.effective_join_line, join_line);
    const int upper = std::max(commit.effective_join_line, join_line);
    if (upper - lower > 4 ||
        static_cast<std::size_t>(upper) > preview_context.instructions.size()) {
        return false;
    }

    for (int line = lower; line <= upper; ++line) {
        if (line < 1 || static_cast<std::size_t>(line) > preview_context.instructions.size()) {
            return false;
        }
        const Lua51Instruction& instruction =
            preview_context.instructions[static_cast<std::size_t>(line - 1)];
        if (instruction.opcode != Lua51Opcode::kLoadBool &&
            instruction.opcode != Lua51Opcode::kJmp) {
            return false;
        }
    }
    return true;
}

LuaJumpRenderResult ContinueResult(int next_current) {
    return LuaJumpRenderResult{
        .disposition = LuaJumpRenderDisposition::kContinue,
        .next_current = next_current,
    };
}

LuaJumpRenderResult ReturnResult() {
    return LuaJumpRenderResult{
        .disposition = LuaJumpRenderDisposition::kReturn,
        .next_current = -1,
    };
}

bool CanApplyLuaTestFallbackPreviewShortcut(const std::vector<Lua51Instruction>& instructions,
                                            int branch_target,
                                            int target_reg) {
    if (branch_target < 1 || static_cast<std::size_t>(branch_target) > instructions.size()) {
        return false;
    }

    const Lua51Instruction& consumer =
        instructions[static_cast<std::size_t>(branch_target - 1)];
    switch (consumer.opcode) {
        case Lua51Opcode::kSetGlobal:
        case Lua51Opcode::kSetUpval:
            return consumer.a == target_reg;
        case Lua51Opcode::kSetTable:
            return consumer.c == target_reg;
        case Lua51Opcode::kCall:
        case Lua51Opcode::kTailCall:
            if (target_reg <= consumer.a) {
                return false;
            }
            if (consumer.b == 0) {
                return true;
            }
            return target_reg < consumer.a + consumer.b;
        case Lua51Opcode::kReturn:
            return consumer.a == target_reg;
        default:
            return false;
    }
}

int ComputeLuaTestFallbackJoinLine(const std::vector<Lua51Instruction>& instructions,
                                   int branch_target,
                                   int target_reg,
                                   bool* consumed_false_loadbool_pair) {
    if (consumed_false_loadbool_pair != nullptr) {
        *consumed_false_loadbool_pair = false;
    }
    if (branch_target < 1 || static_cast<std::size_t>(branch_target) > instructions.size()) {
        return branch_target;
    }
    if (!IsLoadBoolPair(instructions, branch_target)) {
        return branch_target;
    }

    const Lua51Instruction& first =
        instructions[static_cast<std::size_t>(branch_target - 1)];
    if (first.a != target_reg || first.b != 0) {
        return branch_target;
    }

    if (consumed_false_loadbool_pair != nullptr) {
        *consumed_false_loadbool_pair = true;
    }
    return branch_target + 2;
}

bool IsLuaFunctionBoundaryReturnLine(const std::vector<Lua51Instruction>& instructions,
                                     int line,
                                     int target_reg) {
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
    if (target_reg < 0) {
        return true;
    }
    if (instruction.b == 0) {
        return target_reg >= instruction.a;
    }
    return target_reg >= instruction.a &&
           target_reg < instruction.a + instruction.b - 1;
}

bool IsLuaFunctionBoundaryConditionalReturnShape(
    const Lua51Function& function,
    const std::vector<Lua51Instruction>& instructions,
    int branch_target,
    int join_line,
    int target_reg) {
    if (instructions.empty()) {
        return false;
    }
    constexpr std::size_t kMaxFunctionBoundaryParityInstructionCount = 1200;
    constexpr std::size_t kQuestBoundaryParityInstructionCount = 3200;
    const std::size_t max_instruction_count =
        IsLuaBranchPreviewQuestBoundaryParityFunction(function)
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
    return IsLuaFunctionBoundaryReturnLine(instructions, branch_target, target_reg) ||
           IsLuaFunctionBoundaryReturnLine(instructions, join_line, target_reg) ||
           IsLuaFunctionBoundaryReturnLine(instructions, branch_target + 1, target_reg) ||
           IsLuaFunctionBoundaryReturnLine(instructions, join_line + 1, target_reg);
}

bool DoesLuaInstructionReadRegister(const Lua51Instruction& instruction, int reg) {
    switch (instruction.opcode) {
        case Lua51Opcode::kMove:
        case Lua51Opcode::kUnm:
        case Lua51Opcode::kNot:
        case Lua51Opcode::kLen:
        case Lua51Opcode::kSetGlobal:
        case Lua51Opcode::kSetUpval:
        case Lua51Opcode::kTest:
            return instruction.a == reg || instruction.b == reg;
        case Lua51Opcode::kGetTable:
        case Lua51Opcode::kSelf:
        case Lua51Opcode::kAdd:
        case Lua51Opcode::kSub:
        case Lua51Opcode::kMul:
        case Lua51Opcode::kDiv:
        case Lua51Opcode::kMod:
        case Lua51Opcode::kPow:
        case Lua51Opcode::kEq:
        case Lua51Opcode::kLt:
        case Lua51Opcode::kLe:
            return instruction.b == reg || instruction.c == reg;
        case Lua51Opcode::kSetTable:
            return instruction.a == reg || instruction.b == reg || instruction.c == reg;
        case Lua51Opcode::kConcat:
            return reg >= instruction.b && reg <= instruction.c;
        case Lua51Opcode::kTestSet:
            return instruction.b == reg;
        case Lua51Opcode::kCall:
        case Lua51Opcode::kTailCall:
            if (reg < instruction.a) {
                return false;
            }
            if (instruction.b == 0) {
                return true;
            }
            return reg < instruction.a + instruction.b;
        case Lua51Opcode::kReturn:
            if (reg < instruction.a) {
                return false;
            }
            if (instruction.b == 0) {
                return true;
            }
            return reg < instruction.a + instruction.b - 1;
        case Lua51Opcode::kSetList:
            if (instruction.a == reg) {
                return true;
            }
            if (instruction.b == 0) {
                return reg > instruction.a;
            }
            return reg > instruction.a && reg <= instruction.a + instruction.b;
        default:
            return false;
    }
}

bool IsLuaPreviewRegisterReadBeforeWrite(
    const LuaPreviewRenderContext& preview_context,
    int target_reg,
    int start_line,
    int register_count) {
    if (target_reg < 0 || start_line < 1) {
        return false;
    }

    const int instruction_count =
        static_cast<int>(preview_context.instructions.size());
    for (int line = start_line; line <= instruction_count; ++line) {
        const Lua51Instruction& instruction =
            preview_context.instructions[static_cast<std::size_t>(line - 1)];
        if (DoesLuaInstructionReadRegister(instruction, target_reg)) {
            return true;
        }
        const std::vector<bool> written_registers =
            CollectWrittenLuaRegistersInRange(preview_context.instructions,
                                              register_count,
                                              line,
                                              line);
        if (static_cast<std::size_t>(target_reg) < written_registers.size() &&
            written_registers[static_cast<std::size_t>(target_reg)]) {
            return false;
        }
    }
    return false;
}

bool CanCarryLuaTestFallbackPreviewTemporary(const std::vector<Lua51Instruction>& instructions,
                                             const LuaPreviewRenderContext& preview_context,
                                             int branch_target,
                                             int target_reg) {
    if (target_reg < 0 ||
        branch_target < 1 ||
        static_cast<std::size_t>(branch_target) > instructions.size()) {
        return false;
    }

    constexpr int kLuaMaxPreviewTemporaryCarrySpan = 4;
    const int instruction_count = static_cast<int>(instructions.size());
    const int max_line = std::min(instruction_count,
                                  branch_target + kLuaMaxPreviewTemporaryCarrySpan);
    for (int line = branch_target; line <= max_line; ++line) {
        const Lua51Instruction& candidate =
            instructions[static_cast<std::size_t>(line - 1)];
        if (candidate.opcode == Lua51Opcode::kJmp ||
            decompiler::IsConditionalBranchOpcode(candidate.opcode)) {
            return false;
        }
        if (DoesLuaInstructionReadRegister(candidate, target_reg)) {
            return true;
        }
        if (LuaPreviewWritesRegisterAtLine(preview_context, target_reg, line)) {
            return false;
        }
    }

    return false;
}

struct LuaStatementTempCommitDecision {
    bool ownership_proven = false;
    bool call_value_owned = false;
    LuaJoinOwnershipCommitKind join_commit_kind = LuaJoinOwnershipCommitKind::kUnproven;
    bool immediate_sink = false;
    bool materialize_local = false;
    bool can_carry_temporary = false;
    int materialization_line = -1;
};

void NoteLuaCallValueOwnershipCommit(const LuaStatementTempCommitDecision& decision) {
    if (decision.call_value_owned) {
        ++g_lua_native_callvalue_ownership_commits;
    }
}

LuaStatementTempCommitDecision BuildLuaStatementTempCommitDecision(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const LuaPreviewRenderContext& preview_context,
    int condition_line,
    int branch_target,
    int join_line,
    int target_reg,
    int assignment_line,
    std::vector<std::string>* lines,
    std::vector<bool>* declared,
    bool allow_immediate_sink,
    bool allow_temporary_carry,
    const LuaJoinOwnershipCommitDecision* precomputed_ownership_commit,
    bool allow_fallback_rederivation) {
    LuaStatementTempCommitDecision decision;
    (void)control_flow_graph;
    if (!IsLuaBranchPreviewFastPathEnabled(preview_context)) {
        return decision;
    }
    LuaJoinOwnershipCommitDecision ownership_commit =
        precomputed_ownership_commit != nullptr
            ? *precomputed_ownership_commit
            : LuaJoinOwnershipCommitDecision{};
    if (precomputed_ownership_commit == nullptr) {
        if (!allow_fallback_rederivation) {
            return decision;
        }
        LuaConditionalRootCommitPacket root_packet;
        if (!TryResolveLuaBranchPreviewRootPacket(preview_context,
                                                  condition_line,
                                                  branch_target,
                                                  join_line,
                                                  &root_packet)) {
            return decision;
        }
        ownership_commit = root_packet.ownership_commit;
    }
    if (!ownership_commit.proven) {
        return decision;
    }

    decision.ownership_proven = true;
    decision.call_value_owned = ownership_commit.call_value_owned;
    decision.join_commit_kind = ownership_commit.commit_kind;
    decision.immediate_sink =
        allow_immediate_sink &&
        decision.join_commit_kind != LuaJoinOwnershipCommitKind::kCommitInFalseBranch &&
        CanApplyLuaTestFallbackPreviewShortcut(preview_context.instructions, join_line, target_reg);
    decision.materialization_line =
        FindLuaPreviewFallbackMaterializationLine(preview_context, target_reg, assignment_line);
    const LuaScriptPolicy policy = GetLuaBranchPreviewPolicy(preview_context);
    const bool is_scoped_small_holdout =
        policy.profile.Has(LuaScriptProfileFlag::kScopedSmallHoldout);
    const bool is_priority_join_local_target =
        IsLuaPolicyPriorityOwnershipFastPathEnabled(policy, preview_context.instructions.size()) &&
        !policy.profile.Has(LuaScriptProfileFlag::kScopedLargeHoldout);
    const bool is_generic_join_local_target =
        IsLuaPolicyGenericBehaviorFastPathEnabled(policy, preview_context.instructions.size()) &&
        !policy.profile.Has(LuaScriptProfileFlag::kScopedLargeHoldout);
    const bool strong_generic_ownership_proof =
        decision.join_commit_kind == LuaJoinOwnershipCommitKind::kCommitAtJoin ||
        decision.call_value_owned;
    const bool allow_local_materialization =
        is_scoped_small_holdout ||
        is_priority_join_local_target ||
        (is_generic_join_local_target &&
         decision.join_commit_kind != LuaJoinOwnershipCommitKind::kUnproven &&
         strong_generic_ownership_proof);
    decision.materialize_local =
        allow_local_materialization &&
        !decision.immediate_sink &&
        lines != nullptr &&
        declared != nullptr &&
        !GetLuaPreviewFallbackMaterializationName(
             preview_context, target_reg, decision.materialization_line)
             .empty();
    decision.can_carry_temporary =
        allow_temporary_carry &&
        !decision.immediate_sink &&
        !decision.materialize_local &&
        CanCarryLuaTestFallbackPreviewTemporary(
            preview_context.instructions, preview_context, join_line, target_reg);
    return decision;
}

void AssignLuaPreviewFallbackTarget(std::vector<std::string>* lines,
                                    std::vector<StraightLineValue>* values,
                                    std::vector<bool>* declared,
                                    const LuaPreviewRenderContext& preview_context,
                                    int reg,
                                    int name_line,
                                    const StraightLineValue& value) {
    if (lines == nullptr || values == nullptr || declared == nullptr ||
        reg < 0 || static_cast<std::size_t>(reg) >= values->size()) {
        return;
    }

    const std::string name =
        GetLuaPreviewFallbackMaterializationName(preview_context, reg, name_line);
    if (name.empty()) {
        AssignRegister(lines,
                       values,
                       declared,
                       preview_context.declarations,
                       preview_context.function,
                       reg,
                       name_line,
                       value.text,
                       value.multiple_results);
        return;
    }

    const std::string prior_name =
        GetStraightLineRegisterName(
            preview_context.declarations,
            preview_context.function,
            reg,
            std::max(1, name_line - 1));
    const bool has_stable_prior_name = !prior_name.empty() && prior_name == name;
    const bool emit_local =
        !has_stable_prior_name &&
        (prior_name != name || !(*declared)[static_cast<std::size_t>(reg)]) &&
        reg >= preview_context.function.parameter_count;
    AppendIndentedStatement(lines, "  ", RenderAssignmentStatement(name, value.text, emit_local));
    if (emit_local) {
        ++g_lua_native_statementtemp_commits;
        (*declared)[static_cast<std::size_t>(reg)] = true;
    }
    (*values)[static_cast<std::size_t>(reg)] =
        StraightLineValue{name, true, value.multiple_results};
}

bool LuaPreviewFallbackRangeHasNestedControlFlow(const std::vector<Lua51Instruction>& instructions,
                                                 int start_line,
                                                 int end_line) {
    if (start_line > end_line) {
        return false;
    }

    for (int line = start_line;
         line <= end_line && static_cast<std::size_t>(line) <= instructions.size();
         ++line) {
        const Lua51Instruction& candidate = instructions[static_cast<std::size_t>(line - 1)];
        if (candidate.opcode == Lua51Opcode::kJmp ||
            decompiler::IsConditionalBranchOpcode(candidate.opcode)) {
            return true;
        }
    }
    return false;
}

bool TryBuildLuaSimpleTestSetFallbackExpression(
    const LuaBranchPreviewFallbackContext& context,
    const LuaTestSetFallbackContext& testset_context,
    const decompiler::LuaTestSetLoopControlBlock& testset_block,
    int line,
    const std::vector<StraightLineValue>& values,
    const std::vector<bool>& declared,
    StraightLineValue* merged_value) {
    if (merged_value == nullptr ||
        testset_block.fallback_start != testset_block.fallback_end ||
        testset_block.target_register < 0 ||
        testset_block.source_register < 0 ||
        static_cast<std::size_t>(testset_block.target_register) >= values.size() ||
        static_cast<std::size_t>(testset_block.source_register) >= values.size() ||
        !testset_block.invert_condition) {
        return false;
    }

    std::vector<StraightLineValue> fallback_values = values;
    std::vector<bool> fallback_declared = declared;
    const LuaPreviewOperationContext preview_operation_context{
        .instructions = context.preview_context->instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .values = &fallback_values,
        .declared = &fallback_declared,
        .call_arguments_for = context.preview_context->call_arguments_for,
        .render_child = context.preview_context->render_child,
    };

    const Lua51Instruction& fallback_instruction =
        context.preview_context->instructions[static_cast<std::size_t>(
            testset_block.fallback_start - 1)];
    const int fallback_line = static_cast<int>(
        fallback_instruction.line == 0 ? testset_block.fallback_start : fallback_instruction.line);
    LuaPreviewOperationDisposition disposition =
        LuaPreviewOperationDisposition::kBuildFailure;
    std::string build_error;
    int next_current = testset_block.fallback_start + 1;
    if (!TryApplyLuaPreviewOperation(preview_operation_context,
                                     testset_block.fallback_start,
                                     fallback_line,
                                     LuaPreviewOperationMode::kStateTracking,
                                     &disposition,
                                     &build_error,
                                     &next_current) ||
        next_current != testset_block.fallback_end + 1) {
        return false;
    }

    const StraightLineValue initial_value =
        GetLuaTestSetInitialValue(values, testset_context, testset_block, line);
    const StraightLineValue fallback_value =
        fallback_values[static_cast<std::size_t>(testset_block.target_register)];
    if (!initial_value.valid || !fallback_value.valid ||
        initial_value.multiple_results || fallback_value.multiple_results) {
        return false;
    }

    *merged_value = StraightLineValue{
        RenderLogicalExpression(initial_value.text, "or", fallback_value.text),
        true,
        false,
    };
    return true;
}

bool TryBuildLuaGuardedTestCarryExpression(
    const LuaBranchPreviewFallbackContext& context,
    const Lua51Instruction& instruction,
    int current,
    int branch_target,
    int line,
    std::vector<std::string>* lines,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    StraightLineValue* merged_value,
    int* next_current,
    const LuaJoinOwnershipCommitDecision* precomputed_ownership_commit) {
    if (merged_value == nullptr || next_current == nullptr || values == nullptr ||
        declared == nullptr || instruction.opcode != Lua51Opcode::kTest ||
        instruction.a < 0 || context.preview_context == nullptr) {
        return false;
    }

    const int target_reg = instruction.a;
    const int then_start = current + 2;
    if (then_start > branch_target - 1) {
        return false;
    }

    const std::vector<bool> then_written_registers =
        CollectWrittenLuaRegistersInRange(context.preview_context->instructions,
                                          context.register_count,
                                          then_start,
                                          branch_target - 1);
    if (static_cast<std::size_t>(target_reg) >= then_written_registers.size() ||
        !then_written_registers[static_cast<std::size_t>(target_reg)]) {
        return false;
    }

    bool consumed_false_loadbool_pair = false;
    const int join_line =
        ComputeLuaTestFallbackJoinLine(context.preview_context->instructions,
                                       branch_target,
                                       target_reg,
                                       &consumed_false_loadbool_pair);
    if (instruction.c != 0 && !consumed_false_loadbool_pair) {
        return false;
    }

    const LuaPreviewFallbackContext preview_fallback_context{
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .preview_context = context.preview_context,
        .initial_declared = declared,
        .evaluate_inline_range = context.evaluate_inline_range,
    };

    StraightLineValue then_value;
    if (!TryEvaluateLuaPreviewBranchPathValue(preview_fallback_context,
                                              then_start,
                                              join_line,
                                              line,
                                              *values,
                                              target_reg,
                                              &then_value)) {
        return false;
    }

    const bool prefer_function_boundary_parity =
        IsLuaFunctionBoundaryConditionalReturnShape(context.function,
                                                    context.preview_context->instructions,
                                                    branch_target,
                                                    join_line,
                                                    target_reg);
    const std::string condition_text =
        prefer_function_boundary_parity
            ? RenderLuaConditionWithFunctionBoundaryParity(*values,
                                                           context.declarations,
                                                           context.function,
                                                           instruction,
                                                           line)
            : RenderLuaConditionWithValues(*values,
                                           context.declarations,
                                           context.function,
                                           instruction,
                                           line);
    if (condition_text.empty()) {
        return false;
    }

    *merged_value = StraightLineValue{
        RenderLogicalExpression(condition_text, "and", then_value.text),
        true,
        false,
    };

    const int assignment_line = std::max(then_start, join_line - 1);
    const LuaJoinOwnershipCommitDecision* guarded_commit =
        precomputed_ownership_commit != nullptr &&
                precomputed_ownership_commit->proven &&
                precomputed_ownership_commit->target_register == target_reg &&
                IsLuaBranchPreviewJoinLineCompatibleWithOwnershipCommit(
                    *context.preview_context,
                    *precomputed_ownership_commit,
                    join_line)
            ? precomputed_ownership_commit
            : nullptr;
    const LuaStatementTempCommitDecision commit_decision =
        BuildLuaStatementTempCommitDecision(context.control_flow_graph,
                                            *context.preview_context,
                                            current,
                                            branch_target,
                                            join_line,
                                            target_reg,
                                            assignment_line,
                                            lines,
                                            declared,
                                            true,
                                            true,
                                            guarded_commit,
                                            false);
    if (!commit_decision.ownership_proven ||
        (!commit_decision.immediate_sink &&
         !commit_decision.materialize_local &&
         !commit_decision.can_carry_temporary)) {
        return false;
    }

    if (commit_decision.materialize_local) {
        AssignLuaPreviewFallbackTarget(lines,
                                       values,
                                       declared,
                                       *context.preview_context,
                                       target_reg,
                                       commit_decision.materialization_line,
                                       *merged_value);
    } else {
        (*values)[static_cast<std::size_t>(target_reg)] = *merged_value;
    }
    NoteLuaCallValueOwnershipCommit(commit_decision);
    *next_current = join_line;
    return true;
}

}  // namespace

bool TryApplyLuaConditionalPreviewFallback(const LuaBranchPreviewFallbackContext& context,
                                           const Lua51Instruction& instruction,
                                           int current,
                                           int branch_target,
                                           int end_line,
                                           int instruction_count,
                                           int line,
                                           std::vector<std::string>* lines,
                                           std::vector<StraightLineValue>* values,
                                           std::vector<bool>* declared,
                                           int* next_current) {
    if (values == nullptr || next_current == nullptr ||
        context.preview_context == nullptr ||
        !context.evaluate_inline_range || !context.preview_range_is_side_effect_free) {
        return false;
    }

    const bool valid_fallback_range =
        branch_target > current + 2 &&
        branch_target <= end_line + 1 &&
        branch_target <= instruction_count;
    if (!valid_fallback_range) {
        return false;
    }
    if (!IsLuaBranchPreviewFastPathEnabled(*context.preview_context)) {
        return false;
    }
    const bool inline_range_is_side_effect_free =
        context.preview_range_is_side_effect_free(current + 2, branch_target - 1);
    LuaConditionalRootCommitPacket resolved_root_packet;
    const LuaConditionalRootCommitPacket* root_packet_ptr = nullptr;
    if (context.precomputed_root_packet != nullptr &&
        IsLuaConditionalRootPacketReusableForBranch(*context.precomputed_root_packet,
                                                    current,
                                                    branch_target)) {
        root_packet_ptr = context.precomputed_root_packet;
    } else if (decompiler::IsConditionalBranchOpcode(instruction.opcode) &&
               IsLuaBranchPreviewCheapConditionalRootShape(*context.preview_context,
                                                           current,
                                                           branch_target) &&
               TryResolveLuaBranchPreviewRootPacket(*context.preview_context,
                                                    current,
                                                    branch_target,
                                                    branch_target,
                                                    &resolved_root_packet)) {
        root_packet_ptr = &resolved_root_packet;
    }
    const bool have_root_packet = root_packet_ptr != nullptr;
    const bool root_chain_verified =
        have_root_packet && root_packet_ptr->chain_proven && root_packet_ptr->ownership_proven;
    const LuaConditionalChainSpan root_chain_span =
        root_chain_verified ? root_packet_ptr->chain_span : LuaConditionalChainSpan{};
    const LuaJoinOwnershipCommitDecision root_ownership_commit =
        have_root_packet ? root_packet_ptr->ownership_commit : LuaJoinOwnershipCommitDecision{};
    const bool have_root_ownership_commit =
        have_root_packet && root_packet_ptr->ownership_proven;
    const bool root_chain_call_value_owned =
        have_root_packet && root_packet_ptr->call_value_owned;
    const int root_chain_branch_target =
        root_chain_verified && root_packet_ptr->branch_target_line > 0
            ? root_packet_ptr->branch_target_line
            : branch_target;
    const int root_chain_join_line =
        root_chain_verified && root_packet_ptr->effective_join_line > 0
            ? root_packet_ptr->effective_join_line
            : branch_target;

    if (instruction.opcode == Lua51Opcode::kTest && root_chain_verified) {
        StraightLineValue guarded_merged_value;
        if (TryBuildLuaGuardedTestCarryExpression(context,
                                                  instruction,
                                                  current,
                                                  root_chain_branch_target,
                                                  line,
                                                  lines,
                                                  values,
                                                  declared,
                                                  &guarded_merged_value,
                                                  next_current,
                                                  have_root_ownership_commit
                                                      ? &root_ownership_commit
                                                      : nullptr)) {
            return true;
        }
    }

    if (instruction.opcode == Lua51Opcode::kTest &&
        root_chain_verified &&
        instruction.c != 0) {
        bool consumed_false_loadbool_pair = false;
        const int join_line =
            ComputeLuaTestFallbackJoinLine(context.preview_context->instructions,
                                           root_chain_join_line,
                                           instruction.a,
                                           &consumed_false_loadbool_pair);
        const bool branch_target_returns_target =
            root_chain_join_line >= 1 &&
            static_cast<std::size_t>(root_chain_join_line) <=
                context.preview_context->instructions.size() &&
            context.preview_context->instructions[static_cast<std::size_t>(root_chain_join_line - 1)]
                    .opcode == Lua51Opcode::kReturn &&
            context.preview_context->instructions[static_cast<std::size_t>(root_chain_join_line - 1)]
                    .a == instruction.a &&
            context.preview_context->instructions[static_cast<std::size_t>(root_chain_join_line - 1)]
                    .b == 2;
        if (!consumed_false_loadbool_pair && !branch_target_returns_target) {
            return false;
        }
        const std::vector<bool> fallback_written_registers =
            CollectWrittenLuaRegistersInRange(context.preview_context->instructions,
                                              context.register_count,
                                              current + 2,
                                              join_line - 1);
        if (instruction.a < 0 ||
            static_cast<std::size_t>(instruction.a) >= fallback_written_registers.size() ||
            !fallback_written_registers[static_cast<std::size_t>(instruction.a)]) {
            return false;
        }
        const int assignment_line = std::max(current + 2, root_chain_join_line - 1);
        const LuaJoinOwnershipCommitDecision* precomputed_root_commit =
            have_root_ownership_commit &&
                    root_ownership_commit.target_register == instruction.a &&
                    IsLuaBranchPreviewJoinLineCompatibleWithOwnershipCommit(
                        *context.preview_context,
                        root_ownership_commit,
                        join_line)
                ? &root_ownership_commit
                : nullptr;
        const LuaStatementTempCommitDecision commit_decision =
            BuildLuaStatementTempCommitDecision(context.control_flow_graph,
                                                *context.preview_context,
                                                current,
                                                root_chain_join_line,
                                                join_line,
                                                instruction.a,
                                                assignment_line,
                                                lines,
                                                declared,
                                                true,
                                                true,
                                                precomputed_root_commit,
                                                false);
        if (!commit_decision.ownership_proven ||
            (!commit_decision.immediate_sink &&
             !commit_decision.materialize_local &&
             !commit_decision.can_carry_temporary)) {
            return false;
        }
        if (!commit_decision.materialize_local &&
            IsLuaPreviewRegisterReadBeforeWrite(*context.preview_context,
                                                instruction.a,
                                                join_line,
                                                context.register_count)) {
            return false;
        }
        const LuaPreviewFallbackContext preview_fallback_context{
            .control_flow_graph = context.control_flow_graph,
            .declarations = context.declarations,
            .function = context.function,
            .register_count = context.register_count,
            .preview_context = context.preview_context,
            .initial_declared = declared,
            .evaluate_inline_range = context.evaluate_inline_range,
        };
        StraightLineValue merged_value;
        const bool fallback_has_nested_control_flow =
            LuaPreviewFallbackRangeHasNestedControlFlow(context.preview_context->instructions,
                                                        current + 2,
                                                        root_chain_join_line - 1);
        const int fallback_span = std::max(0, root_chain_join_line - (current + 2));
        constexpr int kMaxOwnedCallFallbackBranchPathSpan = 32;
        constexpr int kMaxAmbiguousSpeculativeBranchPathSpan = 32;
        constexpr int kMaxAmbiguousSimpleFallbackSpan = 20;
        const bool stable_owned_span =
            commit_decision.ownership_proven &&
            commit_decision.join_commit_kind != LuaJoinOwnershipCommitKind::kUnproven;
        const bool force_simple_fallback_merge =
            !stable_owned_span && fallback_span > kMaxAmbiguousSimpleFallbackSpan;
        const bool allow_ambiguous_speculative_branch_path =
            fallback_span <= kMaxAmbiguousSpeculativeBranchPathSpan;
        const bool prefer_branch_path_merge =
            !force_simple_fallback_merge &&
            !stable_owned_span &&
            allow_ambiguous_speculative_branch_path &&
            (fallback_has_nested_control_flow ||
             (root_chain_call_value_owned &&
              fallback_span <= kMaxOwnedCallFallbackBranchPathSpan));
        const bool merged_branch_path =
            prefer_branch_path_merge &&
            TryMergeLuaPreviewBranchPathFallback(preview_fallback_context,
                                                 current + 2,
                                                 join_line,
                                                 line,
                                                 *values,
                                                 instruction.a,
                                                 false,
                                                 &merged_value);
        const bool merged_inline =
            !merged_branch_path &&
            inline_range_is_side_effect_free &&
            TryMergeLuaPreviewRangeFallback(preview_fallback_context,
                                            current + 2,
                                            join_line - 1,
                                            line,
                                            *values,
                                            instruction.a,
                                            false,
                                            &merged_value);
        if (force_simple_fallback_merge && !merged_inline) {
            return false;
        }
        if (!merged_branch_path &&
            !merged_inline &&
            !TryMergeLuaPreviewBranchPathFallback(preview_fallback_context,
                                                 current + 2,
                                                 join_line,
                                                 line,
                                                 *values,
                                                 instruction.a,
                                                 false,
                                                 &merged_value)) {
            return false;
        }
        if (commit_decision.materialize_local) {
            AssignLuaPreviewFallbackTarget(lines,
                                           values,
                                           declared,
                                           *context.preview_context,
                                           instruction.a,
                                           commit_decision.materialization_line,
                                           merged_value);
        } else {
            (*values)[static_cast<std::size_t>(instruction.a)] = std::move(merged_value);
        }
        NoteLuaCallValueOwnershipCommit(commit_decision);
        *next_current = join_line;
        return true;
    }

    if (instruction.opcode == Lua51Opcode::kTestSet &&
        root_chain_verified &&
        instruction.c != 0 &&
        instruction.a >= 0 &&
        instruction.b >= 0) {
        const LuaTestSetFallbackContext testset_context{
            .preview_fallback =
                {
                    .control_flow_graph = context.control_flow_graph,
                    .declarations = context.declarations,
                    .function = context.function,
                    .register_count = context.register_count,
                    .preview_context = context.preview_context,
                    .initial_declared = declared,
                    .evaluate_inline_range = context.evaluate_inline_range,
                },
        };
        const decompiler::LuaTestSetLoopControlBlock testset_block{
            .branch_target = root_chain_join_line,
            .fallback_start = current + 2,
            .fallback_end = root_chain_join_line - 1,
            .target_register = instruction.a,
            .source_register = instruction.b,
            .invert_condition = instruction.c != 0,
        };
        const int assignment_line =
            std::max(testset_block.fallback_start, testset_block.fallback_end);
        const LuaJoinOwnershipCommitDecision* precomputed_root_commit =
            have_root_ownership_commit &&
                    root_ownership_commit.target_register == instruction.a &&
                    IsLuaBranchPreviewJoinLineCompatibleWithOwnershipCommit(
                        *context.preview_context,
                        root_ownership_commit,
                        root_chain_join_line)
                ? &root_ownership_commit
                : nullptr;
        const LuaStatementTempCommitDecision commit_decision =
            BuildLuaStatementTempCommitDecision(context.control_flow_graph,
                                                *context.preview_context,
                                                current,
                                                root_chain_join_line,
                                                root_chain_join_line,
                                                instruction.a,
                                                assignment_line,
                                                lines,
                                                declared,
                                                false,
                                                false,
                                                precomputed_root_commit,
                                                false);
        if (!commit_decision.ownership_proven) {
            return false;
        }
        if (!commit_decision.materialize_local &&
            IsLuaPreviewRegisterReadBeforeWrite(*context.preview_context,
                                                instruction.a,
                                                root_chain_join_line,
                                                context.register_count)) {
            return false;
        }
        StraightLineValue merged_value;
        const bool fallback_has_nested_control_flow =
            LuaPreviewFallbackRangeHasNestedControlFlow(context.preview_context->instructions,
                                                        testset_block.fallback_start,
                                                        testset_block.fallback_end);
        const int fallback_span =
            std::max(0, testset_block.fallback_end - testset_block.fallback_start + 1);
        constexpr int kMaxOwnedCallTestSetBranchPathSpan = 32;
        constexpr int kMaxAmbiguousSpeculativeBranchPathSpan = 32;
        const bool stable_owned_span =
            commit_decision.join_commit_kind != LuaJoinOwnershipCommitKind::kUnproven;
        const bool allow_ambiguous_speculative_branch_path =
            fallback_span <= kMaxAmbiguousSpeculativeBranchPathSpan;
        const bool prefer_branch_path_merge =
            !stable_owned_span &&
            allow_ambiguous_speculative_branch_path &&
            (fallback_has_nested_control_flow ||
             (root_chain_call_value_owned &&
              fallback_span <= kMaxOwnedCallTestSetBranchPathSpan));
        const bool merged_branch_path =
            prefer_branch_path_merge &&
            TryMergeLuaTestSetBranchPathFallbackValues(
                testset_context, testset_block, line, *values, &merged_value);
        const bool merged_inline =
            !merged_branch_path &&
            inline_range_is_side_effect_free &&
            TryMergeLuaTestSetFallbackValues(
                testset_context, testset_block, line, *values, &merged_value);
        const bool merged_simple =
            !merged_branch_path &&
            !merged_inline &&
            TryBuildLuaSimpleTestSetFallbackExpression(context,
                                                      testset_context,
                                                      testset_block,
                                                      line,
                                                      *values,
                                                      declared != nullptr
                                                          ? *declared
                                                          : std::vector<bool>{},
                                                      &merged_value);
        if (!merged_branch_path &&
            !merged_inline &&
            !merged_simple &&
            !TryMergeLuaTestSetBranchPathFallbackValues(
                testset_context, testset_block, line, *values, &merged_value)) {
            return false;
        }
        if (commit_decision.materialize_local) {
            AssignLuaPreviewFallbackTarget(lines,
                                           values,
                                           declared,
                                           *context.preview_context,
                                           instruction.a,
                                           commit_decision.materialization_line,
                                           merged_value);
        } else {
            (*values)[static_cast<std::size_t>(instruction.a)] = std::move(merged_value);
        }
        NoteLuaCallValueOwnershipCommit(commit_decision);
        *next_current = root_chain_join_line;
        return true;
    }

    return false;
}

void ResetLuaNativeStatementTempCommitCounter() {
    g_lua_native_statementtemp_commits = 0;
    g_lua_native_callvalue_ownership_commits = 0;
}

std::size_t ConsumeLuaNativeStatementTempCommitCounter() {
    const std::size_t commits = g_lua_native_statementtemp_commits;
    g_lua_native_statementtemp_commits = 0;
    return commits;
}

std::size_t ConsumeLuaNativeCallValueOwnershipCommitCounter() {
    const std::size_t commits = g_lua_native_callvalue_ownership_commits;
    g_lua_native_callvalue_ownership_commits = 0;
    return commits;
}

LuaJumpRenderResult TryRenderLuaSharedTailPreviewConditional(
    const LuaBranchPreviewSharedTailContext& context,
    int branch_target,
    int combined_then_start,
    int end_line,
    std::string_view combined_condition,
    const std::vector<StraightLineValue>& then_entry_values,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.evaluate_inline_range || !context.render_range ||
        !context.collect_written_registers_in_range ||
        !context.last_appended_statement_is_terminal || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return {};
    }

    decompiler::LuaSharedTailConditionalBlock shared_tail_block;
    if (!decompiler::TryBuildLuaSharedTailConditionalBlock(
            context.instructions,
            branch_target,
            combined_then_start,
            end_line,
            loop_continue_target,
            &shared_tail_block)) {
        return {};
    }

    const Lua51Instruction& inner_condition_instruction =
        context.instructions[static_cast<std::size_t>(shared_tail_block.inner_condition_line - 1)];
    std::vector<StraightLineValue> inner_preview_values = then_entry_values;
    std::string inner_preview_error;
    if (combined_then_start <= shared_tail_block.inner_condition_line - 1 &&
        !context.evaluate_inline_range(combined_then_start,
                                       shared_tail_block.inner_condition_line - 1,
                                       &inner_preview_values,
                                       &inner_preview_error)) {
        return {};
    }

    const int inner_render_line =
        static_cast<int>(inner_condition_instruction.line == 0
                             ? shared_tail_block.inner_condition_line
                             : inner_condition_instruction.line);
    const int inner_target_register =
        ResolveLuaConditionalOwnershipRegisterForInstruction(
            inner_condition_instruction, context.register_count);
    const bool prefer_function_boundary_parity =
        IsLuaFunctionBoundaryConditionalReturnShape(context.function,
                                                    context.instructions,
                                                    shared_tail_block.shared_exit,
                                                    shared_tail_block.after_line,
                                                    inner_target_register);
    const std::string inner_condition =
        prefer_function_boundary_parity
            ? RenderLuaConditionWithFunctionBoundaryParity(inner_preview_values,
                                                           context.declarations,
                                                           context.function,
                                                           inner_condition_instruction,
                                                           inner_render_line)
            : RenderLuaConditionWithValues(inner_preview_values,
                                           context.declarations,
                                           context.function,
                                           inner_condition_instruction,
                                           inner_render_line);
    if (inner_condition.empty()) {
        return {};
    }

    const std::string or_condition = RenderLogicalExpression(
        RenderUnaryExpression("not", std::string(combined_condition)), "or", inner_condition);
    const LuaBranchStateSnapshot saved_state{*context.values, *context.declared};
    const LuaBranchStateSnapshot then_entry_state{then_entry_values, *context.declared};
    const LuaConditionalBlockRenderContext conditional_block_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .render_range = context.render_range,
        .collect_written_registers_in_range = context.collect_written_registers_in_range,
        .last_appended_statement_is_terminal = context.last_appended_statement_is_terminal,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    return TryRenderLuaSharedTailConditionalBlock(conditional_block_context,
                                                  shared_tail_block,
                                                  or_condition,
                                                  saved_state,
                                                  then_entry_state,
                                                  invalidate_line,
                                                  loop_continue_target,
                                                  loop_exit_target,
                                                  error);
}

LuaJumpRenderResult TryApplyLuaConditionalPreviewShortcuts(
    const LuaBranchPreviewShortcutContext& context,
    const Lua51Instruction& instruction,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    std::string_view condition,
    std::string_view combined_condition,
    int combined_then_start,
    const std::vector<StraightLineValue>& then_entry_values,
    bool skip_combined_preview_join_shortcuts,
    int invalidate_line,
    int loop_continue_target,
    int loop_exit_target,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        context.preview_context == nullptr || !context.evaluate_inline_range ||
        !context.preview_range_is_side_effect_free || !context.render_range ||
        !context.collect_written_registers_in_range ||
        !context.last_appended_statement_is_terminal || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return {};
    }

    const LuaBranchPreviewFallbackContext preview_fallback_context{
        .control_flow_graph = &context.control_flow_graph,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .preview_context = context.preview_context,
        .precomputed_root_packet = context.precomputed_root_packet,
        .evaluate_inline_range = context.evaluate_inline_range,
        .preview_range_is_side_effect_free = context.preview_range_is_side_effect_free,
    };
    int fallback_next_current = -1;
    if (TryApplyLuaConditionalPreviewFallback(preview_fallback_context,
                                              instruction,
                                              current,
                                              branch_target,
                                              end_line,
                                              instruction_count,
                                              invalidate_line,
                                              context.lines,
                                              context.values,
                                              context.declared,
                                              &fallback_next_current)) {
        return ContinueResult(fallback_next_current);
    }

    const LuaBranchPreviewSharedTailContext shared_tail_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .evaluate_inline_range = context.evaluate_inline_range,
        .render_range = context.render_range,
        .collect_written_registers_in_range = context.collect_written_registers_in_range,
        .last_appended_statement_is_terminal = context.last_appended_statement_is_terminal,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    const LuaJumpRenderResult render_result =
        TryRenderLuaSharedTailPreviewConditional(shared_tail_context,
                                                 branch_target,
                                                 combined_then_start,
                                                 end_line,
                                                 combined_condition,
                                                 then_entry_values,
                                                 invalidate_line,
                                                 loop_continue_target,
                                                 loop_exit_target,
                                                 error);
    if (render_result.disposition != LuaJumpRenderDisposition::kError) {
        return render_result;
    }

    if (skip_combined_preview_join_shortcuts) {
        return {};
    }

    const LuaJumpRenderResult register_join_result =
        TryApplyLuaPreviewRegisterStateJoinShortcut(context.control_flow_graph,
                                                    context.instructions,
                                                    *context.preview_context,
                                                    context.values,
                                                    context.declared,
                                                    combined_condition,
                                                    combined_then_start,
                                                    branch_target,
                                                    end_line,
                                                    instruction_count,
                                                    context.register_count,
                                                    context.preview_range_is_side_effect_free);
    if (register_join_result.disposition != LuaJumpRenderDisposition::kError) {
        return register_join_result;
    }

    const LuaJumpRenderResult join_result =
        TryApplyLuaPreviewConditionalJoinShortcuts(context.lines,
                                                   context.control_flow_graph,
                                                   context.instructions,
                                                   *context.preview_context,
                                                   context.values,
                                                   context.declared,
                                                   combined_condition,
                                                   combined_then_start,
                                                   branch_target,
                                                   end_line,
                                                   instruction_count,
                                                   context.register_count,
                                                   context.preview_range_is_side_effect_free,
                                                   context.invalidate_values,
                                                   invalidate_line);
    if (join_result.disposition != LuaJumpRenderDisposition::kError) {
        return join_result;
    }

    const LuaJumpRenderResult call_join_result =
        TryApplyLuaPreviewCallJoinShortcut(context.lines,
                                           context.instructions,
                                           *context.preview_context,
                                           context.values,
                                           context.declared,
                                           combined_condition,
                                           combined_then_start,
                                           branch_target,
                                           end_line,
                                           instruction_count,
                                           context.register_count,
                                           context.invalidate_values);
    if (call_join_result.disposition != LuaJumpRenderDisposition::kError) {
        if (context.finalize_pending_jump(loop_continue_target, loop_exit_target)) {
            return ReturnResult();
        }
        return call_join_result;
    }

    return {};
}

}  // namespace refii::lua
