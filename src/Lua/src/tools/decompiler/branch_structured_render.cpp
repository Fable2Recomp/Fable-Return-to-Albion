#include "tools/decompiler/branch_structured_render.h"

#include "tools/decompiler/branch_loop_render.h"
#include "tools/decompiler/condition_render.h"

namespace refii::lua {
namespace {

LuaJumpRenderResult ErrorResult(std::string* error, std::string message) {
    if (error != nullptr) {
        *error = std::move(message);
    }
    return {};
}

}  // namespace

LuaJumpRenderResult TryRenderLuaStructuredConditionalBranch(
    const LuaStructuredConditionalBranchContext& context,
    int current,
    int branch_target,
    int end_line,
    int instruction_count,
    int combined_then_start,
    std::string_view condition,
    std::string_view combined_condition,
    const std::vector<StraightLineValue>& then_entry_values,
    int loop_continue_target,
    int loop_exit_target,
    int invalidate_line,
    std::string* error) {
    if (context.lines == nullptr || context.values == nullptr || context.declared == nullptr ||
        !context.render_range || !context.collect_written_registers_in_range ||
        !context.last_appended_statement_is_terminal || !context.invalidate_values ||
        !context.finalize_pending_jump) {
        return ErrorResult(error, "structured_branch_context_missing");
    }

    auto& values = *context.values;
    auto& declared = *context.declared;
    const auto restore_state = [&](const LuaBranchStateSnapshot& state) {
        values = state.values;
        declared = state.declared;
    };

    const LuaBranchLoopRenderContext branch_loop_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .render_range = context.render_range,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    const auto compute_jump_target = [&](int pc, int sbx) {
        return decompiler::ComputeJumpTargetLine(pc, sbx, instruction_count);
    };
    const auto has_loop_continue_else_tail = [&]() {
        if (loop_continue_target == -1 ||
            branch_target <= combined_then_start ||
            branch_target > end_line ||
            branch_target - 1 < 1 ||
            branch_target - 1 > instruction_count) {
            return false;
        }

        const Lua51Instruction& trailing_then_jump =
            context.instructions[static_cast<std::size_t>(branch_target - 2)];
        if (trailing_then_jump.opcode != Lua51Opcode::kJmp) {
            return false;
        }

        const int trailing_then_target =
            compute_jump_target(branch_target - 1, trailing_then_jump.sbx);
        const int resolved_trailing_then_target =
            decompiler::ResolveTerminalJumpTarget(context.instructions, trailing_then_target);
        return trailing_then_target == loop_continue_target ||
               resolved_trailing_then_target == loop_continue_target;
    };

    decompiler::LuaBackJumpLoopBlock back_jump_loop_block;
    if (decompiler::TryBuildLuaBackJumpLoopBlock(context.instructions,
                                                 current,
                                                 branch_target,
                                                 end_line,
                                                 &back_jump_loop_block)) {
        return TryRenderLuaBackJumpLoopBranch(
            branch_loop_context, back_jump_loop_block, condition, invalidate_line, error);
    }

    if (!has_loop_continue_else_tail() &&
        branch_target - 1 >= current + 2 &&
        branch_target - 1 <= end_line &&
        branch_target - 1 <= instruction_count) {
        const Lua51Instruction& trailing_loop_jump =
            context.instructions[static_cast<std::size_t>(branch_target - 2)];
        if (trailing_loop_jump.opcode == Lua51Opcode::kJmp) {
            const int preamble_loop_target =
                compute_jump_target(branch_target - 1, trailing_loop_jump.sbx);
            if (preamble_loop_target >= 1 && preamble_loop_target < current) {
                const LuaBranchStateSnapshot saved_state{values, declared};
                const LuaBranchStateSnapshot then_entry_state{then_entry_values, declared};
                restore_state(then_entry_state);
                const LuaJumpRenderResult render_result =
                    TryRenderLuaPreambleLoopBranch(branch_loop_context,
                                                   combined_condition,
                                                   current + 2,
                                                   branch_target - 2,
                                                   preamble_loop_target,
                                                   branch_target,
                                                   invalidate_line,
                                                   branch_target,
                                                   error);
                restore_state(saved_state);
                if (render_result.disposition != LuaJumpRenderDisposition::kError) {
                    return render_result;
                }
            }
        }
    }

    decompiler::LuaConditionalBlock conditional_block;
    if (!decompiler::TryBuildLuaConditionalBlock(context.control_flow_graph,
                                                 context.instructions,
                                                 current,
                                                 combined_then_start,
                                                 branch_target,
                                                 end_line,
                                                 loop_continue_target,
                                                 loop_exit_target,
                                                 &conditional_block)) {
        return ErrorResult(error, "conditional_block_shape_failed");
    }
    decompiler::LuaConditionalOwnership ownership;
    const bool have_ownership = decompiler::TryClassifyLuaConditionalOwnership(
        context.control_flow_graph,
        context.instructions,
        current,
        combined_then_start,
        branch_target,
        end_line,
        loop_continue_target,
        loop_exit_target,
        &ownership);
    LuaConditionalRootCommitPacket statement_temp_packet;
    bool have_statement_temp_ownership = false;
    if (context.precomputed_root_packet != nullptr &&
        context.precomputed_root_packet->chain_proven &&
        IsLuaConditionalRootPacketReusableForBranch(*context.precomputed_root_packet,
                                                    current,
                                                    branch_target)) {
        statement_temp_packet = *context.precomputed_root_packet;
        have_statement_temp_ownership = true;
    }
    if (!have_statement_temp_ownership) {
        have_statement_temp_ownership =
            TryResolveLuaConditionalRootCommitPacket(&context.control_flow_graph,
                                                     context.instructions,
                                                     instruction_count,
                                                     current,
                                                     std::max(current, branch_target - 2),
                                                     std::max(branch_target,
                                                              conditional_block.after_line),
                                                     branch_target,
                                                     context.register_count,
                                                     branch_target,
                                                     conditional_block.after_line,
                                                     &statement_temp_packet,
                                                     &context.function);
    }
    const LuaJoinOwnershipCommitDecision statement_temp_commit =
        have_statement_temp_ownership
            ? statement_temp_packet.ownership_commit
            : LuaJoinOwnershipCommitDecision{};
    if (have_ownership && ownership.classified && !ownership.allow_else &&
        conditional_block.has_else) {
        conditional_block.else_start = -1;
        conditional_block.else_end = -1;
        conditional_block.has_else = false;
        conditional_block.prefer_elseif_chain = false;
        conditional_block.after_line = branch_target;
    }
    if (!conditional_block.prefer_elseif_chain &&
        conditional_block.has_else &&
        conditional_block.else_start == branch_target &&
        conditional_block.after_line > branch_target &&
        conditional_block.after_line <= end_line + 1) {
        const int partner_jump_line = branch_target - 1;
        const bool has_ambiguous_external_owner =
            decompiler::HasIncomingEdgeIntoRange(context.control_flow_graph,
                                                 branch_target,
                                                 conditional_block.after_line - 1,
                                                 current,
                                                 partner_jump_line);
        // unluac-style else-if partner shaping: only prefer direct elseif emission
        // when the shared join is clearly owned by this conditional branch.
        const bool statement_temp_commit_allows_elseif =
            !have_statement_temp_ownership ||
            (statement_temp_commit.proven &&
             statement_temp_commit.commit_kind !=
                 LuaJoinOwnershipCommitKind::kCommitInFalseBranch);
        if (!has_ambiguous_external_owner &&
            (!have_ownership || !ownership.classified || ownership.allow_elseif) &&
            statement_temp_commit_allows_elseif) {
            conditional_block.prefer_elseif_chain = true;
        }
    }

    const LuaBranchStateSnapshot saved_state{values, declared};
    const LuaBranchStateSnapshot then_entry_state{then_entry_values, declared};
    const LuaConditionalBlockRenderContext conditional_block_context{
        .lines = context.lines,
        .values = context.values,
        .declared = context.declared,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .render_range = context.render_range,
        .collect_written_registers_in_range = context.collect_written_registers_in_range,
        .last_appended_statement_is_terminal = context.last_appended_statement_is_terminal,
        .invalidate_values = context.invalidate_values,
        .finalize_pending_jump = context.finalize_pending_jump,
    };
    return TryRenderLuaConditionalBlock(conditional_block_context,
                                        conditional_block,
                                        combined_condition,
                                        saved_state,
                                        then_entry_state,
                                        invalidate_line,
                                        loop_continue_target,
                                        loop_exit_target,
                                        error);
}

}  // namespace refii::lua
