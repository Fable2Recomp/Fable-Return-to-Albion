#include "tools/decompiler/preview_join_render.h"

#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>

namespace refii::lua {
namespace {

constexpr int kLuaMaxPreviewJoinCandidates = 12;
constexpr int kLuaMaxPreviewJoinSpan = 48;

LuaJumpRenderResult ContinueResult(int next_current) {
    return LuaJumpRenderResult{
        .disposition = LuaJumpRenderDisposition::kContinue,
        .next_current = next_current,
    };
}

}  // namespace

bool ShouldAttemptLuaPreviewStatementJoin(int then_start_line,
                                          int then_end_line,
                                          int else_start_line,
                                          int else_end_line,
                                          int statement_line) {
    if (statement_line < 1) {
        return false;
    }

    int min_line = statement_line;
    int max_line = statement_line;
    const auto consider_range = [&](int start_line, int end_line) {
        if (start_line < 1 || end_line < start_line) {
            return true;
        }
        if (end_line - start_line > kLuaMaxPreviewJoinSpan) {
            return false;
        }
        min_line = std::min(min_line, start_line);
        max_line = std::max(max_line, end_line);
        return true;
    };

    if (!consider_range(then_start_line, then_end_line) ||
        !consider_range(else_start_line, else_end_line)) {
        return false;
    }

    return max_line - min_line <= kLuaMaxPreviewJoinSpan;
}

LuaJumpRenderResult EmitLuaPreviewStatementJoin(std::vector<std::string>* lines,
                                                const LuaPreviewRenderContext& preview_context,
                                                std::vector<StraightLineValue>* values,
                                                std::vector<bool>* declared,
                                                std::string_view condition,
                                                const LuaPreviewStatementJoin& join,
                                                int register_count,
                                                const LuaInvalidateValuesRenderer& invalidate_values,
                                                int invalidate_line,
                                                int next_current) {
    if (lines == nullptr || !invalidate_values) {
        return {};
    }

    std::string then_statement;
    std::string else_statement;
    if (join.then_requires_full_tail_render || join.else_requires_full_tail_render) {
        return {};
    }
    if (!TryRenderLuaPreviewStatementJoin(
            preview_context, join, &then_statement, &else_statement)) {
        return {};
    }

    AppendIndentedStatement(lines, "  ", "if " + std::string(condition) + " then");
    AppendIndentedStatement(lines, "  ", then_statement);
    AppendIndentedStatement(lines, "  ", "else");
    AppendIndentedStatement(lines, "  ", else_statement);
    AppendIndentedStatement(lines, "  ", "end");
    if (values != nullptr && declared != nullptr) {
        LuaBranchStateSnapshot then_state;
        LuaBranchStateSnapshot else_state;
        const LuaBranchStateSnapshot base_state{*values, *declared};
        if (TryBuildLuaPreviewStatementJoinStates(
                preview_context, join, &then_state, &else_state)) {
            const LuaMergedBranchState merged_state =
                BuildLuaMergedBranchState(base_state,
                                          then_state,
                                          join.then_written_registers,
                                          else_state,
                                          join.else_written_registers,
                                          preview_context.declarations,
                                          preview_context.function,
                                          next_current,
                                          register_count);
            *values = merged_state.values;
            *declared = merged_state.declared;
            return ContinueResult(next_current);
        }
    }

    invalidate_values(invalidate_line);
    return ContinueResult(next_current);
}

LuaJumpRenderResult TryBuildAndEmitLuaPreviewStatementJoin(
    std::vector<std::string>* lines,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int then_end_line,
    int else_start_line,
    int else_end_line,
    int statement_line,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line,
    int next_current) {
    if (!ShouldAttemptLuaPreviewStatementJoin(then_start_line,
                                              then_end_line,
                                              else_start_line,
                                              else_end_line,
                                              statement_line)) {
        return {};
    }

    LuaPreviewStatementJoin join;
    std::string preview_error;
    if (!TryBuildLuaPreviewStatementJoin(preview_context,
                                         values != nullptr ? *values
                                                          : std::vector<StraightLineValue>{},
                                         declared != nullptr ? *declared : std::vector<bool>{},
                                         then_start_line,
                                         then_end_line,
                                         else_start_line,
                                         else_end_line,
                                         statement_line,
                                         &join,
                                         &preview_error)) {
        return {};
    }

    return EmitLuaPreviewStatementJoin(lines,
                                       preview_context,
                                       values,
                                       declared,
                                       condition,
                                       join,
                                       register_count,
                                       invalidate_values,
                                       invalidate_line,
                                       next_current);
}

LuaJumpRenderResult TryBuildAndEmitLuaPreviewStatementJoinToLine(
    std::vector<std::string>* lines,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int else_start_line,
    int join_line,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line,
    int next_current) {
    LuaPreviewStatementJoin join;
    std::string preview_error;
    if (!TryBuildLuaPreviewStatementJoinToLine(preview_context,
                                               values != nullptr ? *values
                                                                : std::vector<StraightLineValue>{},
                                               declared != nullptr ? *declared
                                                                   : std::vector<bool>{},
                                               then_start_line,
                                               else_start_line,
                                               join_line,
                                               &join,
                                               &preview_error)) {
        return {};
    }

    return EmitLuaPreviewStatementJoin(lines,
                                       preview_context,
                                       values,
                                       declared,
                                       condition,
                                       join,
                                       register_count,
                                       invalidate_values,
                                       invalidate_line,
                                       next_current);
}

LuaJumpRenderResult TryApplyLuaPreviewConditionalJoinShortcuts(
    std::vector<std::string>* lines,
    const decompiler::Lua51ControlFlowGraph& control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    int register_count,
    const LuaPreviewJoinSideEffectFreeChecker& preview_range_is_side_effect_free,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line) {
    if (lines == nullptr || values == nullptr || declared == nullptr ||
        !preview_range_is_side_effect_free || !invalidate_values) {
        return {};
    }

    if (branch_target == current + 3 &&
        current + 4 <= end_line &&
        current + 4 <= instruction_count &&
        IsLoadBoolPair(instructions, current + 2)) {
        const LuaJumpRenderResult join_result =
            TryBuildAndEmitLuaPreviewStatementJoin(lines,
                                                  preview_context,
                                                  values,
                                                  declared,
                                                  condition,
                                                  current + 2,
                                                  current + 2,
                                                  branch_target,
                                                  branch_target,
                                                  current + 4,
                                                  register_count,
                                                  invalidate_values,
                                                  invalidate_line,
                                                  current + 5);
        if (join_result.disposition != LuaJumpRenderDisposition::kError) {
            return join_result;
        }
    }

    if (branch_target > current + 2) {
        const std::vector<int> join_candidate_lines =
            decompiler::CollectSharedJoinCandidateLines(control_flow_graph,
                                                        current + 2,
                                                        branch_target,
                                                        branch_target + 1,
                                                        std::min(end_line, instruction_count));
        int attempted_join_candidates = 0;
        for (int join_line : join_candidate_lines) {
            if (attempted_join_candidates >= kLuaMaxPreviewJoinCandidates) {
                break;
            }
            if (!ShouldAttemptLuaPreviewStatementJoin(current + 2,
                                                     join_line - 1,
                                                     branch_target,
                                                     join_line - 1,
                                                     join_line)) {
                continue;
            }
            ++attempted_join_candidates;
            const LuaJumpRenderResult join_result =
                TryBuildAndEmitLuaPreviewStatementJoinToLine(lines,
                                                             preview_context,
                                                             values,
                                                             declared,
                                                             condition,
                                                             current + 2,
                                                             branch_target,
                                                             join_line,
                                                             register_count,
                                                             invalidate_values,
                                                             invalidate_line,
                                                             join_line + 1);
            if (join_result.disposition != LuaJumpRenderDisposition::kError) {
                return join_result;
            }
        }
    }

    if (branch_target > current + 2 &&
        branch_target <= end_line &&
        branch_target <= instruction_count &&
        ShouldAttemptLuaPreviewStatementJoin(current + 2,
                                            branch_target - 1,
                                            branch_target,
                                            branch_target - 1,
                                            branch_target) &&
        preview_range_is_side_effect_free(current + 2, branch_target - 1)) {
        const LuaJumpRenderResult join_result =
            TryBuildAndEmitLuaPreviewStatementJoin(lines,
                                                  preview_context,
                                                  values,
                                                  declared,
                                                  condition,
                                                  current + 2,
                                                  branch_target - 1,
                                                  branch_target,
                                                  branch_target - 1,
                                                  branch_target,
                                                  register_count,
                                                  invalidate_values,
                                                  invalidate_line,
                                                  branch_target + 1);
        if (join_result.disposition != LuaJumpRenderDisposition::kError) {
            return join_result;
        }
    }

    return {};
}

LuaJumpRenderResult TryApplyLuaPreviewCallJoinShortcut(
    std::vector<std::string>* lines,
    const std::vector<Lua51Instruction>& instructions,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values) {
    if (lines == nullptr || values == nullptr || declared == nullptr || !invalidate_values) {
        return {};
    }

    if (branch_target <= current + 2 ||
        branch_target > end_line ||
        branch_target > instruction_count) {
        return {};
    }

    int then_jump_line = -1;
    int call_line = -1;
    const auto compute_jump_target = [&](int pc, int sbx) {
        return decompiler::ComputeJumpTargetLine(pc, sbx, instruction_count);
    };

    for (int candidate = current + 2; candidate <= branch_target - 1; ++candidate) {
        const Lua51Instruction& maybe_then_jump =
            instructions[static_cast<std::size_t>(candidate - 1)];
        if (maybe_then_jump.opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const int candidate_call_line =
            compute_jump_target(candidate, maybe_then_jump.sbx);
        if (candidate_call_line <= branch_target ||
            candidate_call_line > end_line ||
            candidate_call_line > instruction_count) {
            continue;
        }

        const Lua51Instruction& maybe_call =
            instructions[static_cast<std::size_t>(candidate_call_line - 1)];
        if (maybe_call.opcode != Lua51Opcode::kCall &&
            maybe_call.opcode != Lua51Opcode::kTailCall) {
            continue;
        }

        then_jump_line = candidate;
        call_line = candidate_call_line;
        break;
    }

    if (then_jump_line == -1 || call_line == -1 || branch_target > call_line - 1) {
        return {};
    }

    const int then_preview_end = then_jump_line - 1;
    return TryBuildAndEmitLuaPreviewStatementJoin(lines,
                                                  preview_context,
                                                  values,
                                                  declared,
                                                  condition,
                                                  current + 2,
                                                  then_preview_end,
                                                  branch_target,
                                                  call_line - 1,
                                                  call_line,
                                                  register_count,
                                                  invalidate_values,
                                                  call_line,
                                                  call_line + 1);
}

}  // namespace refii::lua
