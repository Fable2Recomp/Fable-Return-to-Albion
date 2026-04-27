#include "tools/decompiler/control_flow_blocks.h"

#include <algorithm>
#include <cstdint>
#include <unordered_map>

namespace refii::lua::decompiler {

namespace {

constexpr int kLuaOwnershipEarlyBailoutSpan = 1024;

int ComputeJumpTarget(int pc, int sbx, int instruction_count) {
    return ComputeJumpTargetLine(pc, sbx, instruction_count);
}

int ResolveJumpTarget(const std::vector<Lua51Instruction>& instructions, int target_line) {
    return ResolveTerminalJumpTarget(instructions, target_line);
}

int ComputeLuaConditionalElseJoinFloor(const std::vector<Lua51Instruction>& instructions,
                                       int branch_target,
                                       int end_line,
                                       int instruction_count) {
    if (branch_target < 1 ||
        static_cast<std::size_t>(branch_target + 1) > instructions.size()) {
        return branch_target + 1;
    }

    const Lua51Instruction& else_condition =
        instructions[static_cast<std::size_t>(branch_target - 1)];
    const Lua51Instruction& else_jump =
        instructions[static_cast<std::size_t>(branch_target)];
    if (!IsConditionalBranchOpcode(else_condition.opcode) ||
        else_jump.opcode != Lua51Opcode::kJmp) {
        return branch_target + 1;
    }

    const int raw_target =
        ComputeJumpTargetLine(branch_target + 1, else_jump.sbx, instruction_count);
    const int resolved_target = ResolveJumpTarget(instructions, raw_target);
    const int effective_target =
        (resolved_target > branch_target && resolved_target <= end_line + 1)
            ? resolved_target
            : raw_target;
    if (effective_target <= branch_target + 1) {
        return branch_target + 1;
    }

    return effective_target;
}

int ComputeLuaStatementJoinFloor(const std::vector<Lua51Instruction>& instructions,
                                 int start_line) {
    if (start_line < 1 || static_cast<std::size_t>(start_line) > instructions.size()) {
        return start_line + 1;
    }

    int current = start_line;
    bool advanced = false;
    while (static_cast<std::size_t>(current) <= instructions.size()) {
        const Lua51Instruction& instruction =
            instructions[static_cast<std::size_t>(current - 1)];
        switch (instruction.opcode) {
            case Lua51Opcode::kMove:
            case Lua51Opcode::kLoadK:
            case Lua51Opcode::kLoadBool:
            case Lua51Opcode::kGetUpval:
            case Lua51Opcode::kGetGlobal:
            case Lua51Opcode::kGetTable:
            case Lua51Opcode::kSelf:
            case Lua51Opcode::kNewTable:
            case Lua51Opcode::kClosure:
            case Lua51Opcode::kVarArg:
            case Lua51Opcode::kUnm:
            case Lua51Opcode::kNot:
            case Lua51Opcode::kLen:
            case Lua51Opcode::kConcat:
                advanced = true;
                ++current;
                continue;
            case Lua51Opcode::kCall:
            case Lua51Opcode::kTailCall:
            case Lua51Opcode::kSetGlobal:
            case Lua51Opcode::kSetUpval:
            case Lua51Opcode::kSetTable:
            case Lua51Opcode::kReturn:
                return current + 1;
            default:
                return advanced ? current : start_line + 1;
        }
    }

    return advanced ? current : start_line + 1;
}

bool IsOwnedLuaConditionalLoopbackTarget(int target,
                                         int conditional_line,
                                         int then_start,
                                         int loop_continue_target) {
    return target == conditional_line ||
           target == then_start ||
           (loop_continue_target != -1 && target == loop_continue_target);
}

int ComputeLuaConditionalOwnershipJoinLine(const Lua51ControlFlowGraph& graph,
                                           const std::vector<Lua51Instruction>& instructions,
                                           int conditional_line,
                                           int then_start,
                                           int branch_target,
                                           int end_line,
                                           int instruction_count) {
    if (branch_target < 1) {
        return -1;
    }

    int join_line = branch_target;
    if (branch_target - 1 >= 1 &&
        static_cast<std::size_t>(branch_target - 1) <= instructions.size()) {
        const Lua51Instruction& trailing_then_jump =
            instructions[static_cast<std::size_t>(branch_target - 2)];
        if (trailing_then_jump.opcode == Lua51Opcode::kJmp) {
            const int raw_target = ComputeJumpTargetLine(
                branch_target - 1, trailing_then_jump.sbx, instruction_count);
            const int resolved_target = ResolveJumpTarget(instructions, raw_target);
            const int effective_target =
                (resolved_target > branch_target && resolved_target <= end_line + 1)
                    ? resolved_target
                    : raw_target;
            if (effective_target > branch_target) {
                join_line = effective_target;
            }
        }
    }

    if (join_line != branch_target) {
        return join_line;
    }

    const int else_join_floor =
        std::max(ComputeLuaConditionalElseJoinFloor(
                     instructions, branch_target, end_line, instruction_count),
                 ComputeLuaStatementJoinFloor(instructions, branch_target));
    const int join_search_end = std::min(end_line + 1, instruction_count);
    if (else_join_floor <= join_search_end) {
        const int shared_join =
            FindSharedJoinLine(graph,
                               then_start,
                               branch_target,
                               else_join_floor,
                               join_search_end);
        if (shared_join > branch_target) {
            return shared_join;
        }
    }

    return branch_target;
}

bool ClassifyLuaConditionalOwnershipCore(const Lua51ControlFlowGraph& graph,
                                         const std::vector<Lua51Instruction>& instructions,
                                         int conditional_line,
                                         int then_start,
                                         int branch_target,
                                         int end_line,
                                         int loop_continue_target,
                                         int loop_exit_target,
                                         int consumed_span_start,
                                         int consumed_span_end,
                                         LuaConditionalOwnership* ownership) {
    if (ownership == nullptr) {
        return false;
    }

    *ownership = LuaConditionalOwnership{};
    ownership->classified = true;
    if (branch_target < 1 || then_start < 1 || end_line < conditional_line) {
        return false;
    }

    const int span_width = end_line - conditional_line;
    if (span_width > kLuaOwnershipEarlyBailoutSpan) {
        ownership->early_bailout = true;
        ownership->allow_else = false;
        ownership->allow_elseif = false;
        ownership->region_start = branch_target;
        ownership->region_end = end_line;
        return true;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    const int join_line = ComputeLuaConditionalOwnershipJoinLine(graph,
                                                                 instructions,
                                                                 conditional_line,
                                                                 then_start,
                                                                 branch_target,
                                                                 end_line,
                                                                 instruction_count);
    ownership->effective_join_line = join_line;
    ownership->region_start = branch_target;
    ownership->region_end = std::max(branch_target, join_line - 1);

    const int ignore_source_line_end = std::max(conditional_line, branch_target - 1);
    const bool have_consumed_span =
        consumed_span_start >= conditional_line &&
        consumed_span_end >= consumed_span_start &&
        consumed_span_end <= std::max(consumed_span_start, join_line - 1);
    const int ownership_scan_start = have_consumed_span ? consumed_span_start : branch_target;
    const int ownership_scan_end =
        have_consumed_span ? consumed_span_end : std::max(branch_target, join_line - 1);
    ownership->region_start = ownership_scan_start;
    ownership->region_end = ownership_scan_end;
    if (join_line > branch_target) {
        if (ownership_scan_start <= ownership_scan_end) {
            ownership->has_foreign_incoming_edge = HasIncomingEdgeIntoRange(
                graph,
                ownership_scan_start,
                ownership_scan_end,
                conditional_line,
                std::max(ignore_source_line_end, ownership_scan_end));
        }
        if (!ownership->has_foreign_incoming_edge && !have_consumed_span &&
            branch_target - 1 >= then_start) {
            ownership->has_foreign_incoming_edge = HasIncomingEdgeIntoRange(
                graph,
                then_start,
                branch_target - 1,
                conditional_line,
                ignore_source_line_end);
        }
        if (!ownership->has_foreign_incoming_edge && have_consumed_span &&
            join_line >= conditional_line &&
            join_line <= static_cast<int>(instructions.size()) + 1) {
            ownership->has_foreign_incoming_edge = HasIncomingEdgeIntoRange(
                graph,
                join_line,
                join_line,
                conditional_line,
                std::max(ignore_source_line_end, join_line - 1));
        }
    }

    const int loop_scan_end = std::min(end_line, std::max(branch_target, join_line - 1));
    for (int line = then_start;
         line <= loop_scan_end &&
         static_cast<std::size_t>(line) <= instructions.size();
         ++line) {
        const Lua51Instruction& instruction = instructions[static_cast<std::size_t>(line - 1)];
        if (instruction.opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const int raw_target = ComputeJumpTargetLine(line, instruction.sbx, instruction_count);
        const int resolved_target = ResolveJumpTarget(instructions, raw_target);
        const int effective_target = resolved_target > 0 ? resolved_target : raw_target;
        if (effective_target > line) {
            continue;
        }

        if (!IsOwnedLuaConditionalLoopbackTarget(effective_target,
                                                 conditional_line,
                                                 then_start,
                                                 loop_continue_target)) {
            ownership->has_unowned_loopback = true;
            break;
        }
    }

    if (!ownership->has_unowned_loopback &&
        loop_exit_target != -1 &&
        join_line > loop_exit_target) {
        ownership->has_unowned_loopback = true;
    }

    ownership->unambiguous =
        !ownership->early_bailout &&
        !ownership->has_foreign_incoming_edge &&
        !ownership->has_unowned_loopback;
    ownership->allow_else = ownership->unambiguous || join_line == branch_target;
    ownership->allow_elseif = ownership->unambiguous && join_line > branch_target;
    return true;
}

struct LuaConditionalOwnershipCacheKey {
    int conditional_line = 0;
    int then_start = 0;
    int branch_target = 0;
    int end_line = 0;
    int loop_continue_target = 0;
    int loop_exit_target = 0;
    int consumed_span_start = 0;
    int consumed_span_end = 0;

    bool operator==(const LuaConditionalOwnershipCacheKey& other) const {
        return conditional_line == other.conditional_line &&
               then_start == other.then_start &&
               branch_target == other.branch_target &&
               end_line == other.end_line &&
               loop_continue_target == other.loop_continue_target &&
               loop_exit_target == other.loop_exit_target &&
               consumed_span_start == other.consumed_span_start &&
               consumed_span_end == other.consumed_span_end;
    }
};

struct LuaConditionalOwnershipCacheKeyHash {
    std::size_t operator()(const LuaConditionalOwnershipCacheKey& key) const {
        std::size_t hash = static_cast<std::size_t>(key.conditional_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.then_start);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.branch_target);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.end_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.loop_continue_target + 0x9e37);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.loop_exit_target + 0x7f4a);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.consumed_span_start + 0x1357);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.consumed_span_end + 0x2468);
        return hash;
    }
};

struct LuaConditionalOwnershipMemo {
    const Lua51Instruction* instructions_data = nullptr;
    std::size_t instruction_count = 0;
    const Lua51ControlFlowGraph* graph = nullptr;
    std::unordered_map<LuaConditionalOwnershipCacheKey,
                       LuaConditionalOwnership,
                       LuaConditionalOwnershipCacheKeyHash>
        cache;
};

LuaConditionalOwnershipMemo& GetLuaConditionalOwnershipMemo() {
    static thread_local LuaConditionalOwnershipMemo memo;
    return memo;
}

}  // namespace

bool TryClassifyLuaConditionalOwnership(const Lua51ControlFlowGraph& graph,
                                        const std::vector<Lua51Instruction>& instructions,
                                        int conditional_line,
                                        int then_start,
                                        int branch_target,
                                        int end_line,
                                        int loop_continue_target,
                                        int loop_exit_target,
                                        LuaConditionalOwnership* ownership) {
    return TryClassifyLuaConditionalOwnershipForConsumedSpan(graph,
                                                             instructions,
                                                             conditional_line,
                                                             then_start,
                                                             branch_target,
                                                             end_line,
                                                             loop_continue_target,
                                                             loop_exit_target,
                                                             -1,
                                                             -1,
                                                             ownership);
}

bool TryClassifyLuaConditionalOwnershipForConsumedSpan(
    const Lua51ControlFlowGraph& graph,
    const std::vector<Lua51Instruction>& instructions,
    int conditional_line,
    int then_start,
    int branch_target,
    int end_line,
    int loop_continue_target,
    int loop_exit_target,
    int consumed_span_start,
    int consumed_span_end,
    LuaConditionalOwnership* ownership) {
    if (ownership == nullptr) {
        return false;
    }

    LuaConditionalOwnershipMemo& memo = GetLuaConditionalOwnershipMemo();
    if (memo.instructions_data != instructions.data() ||
        memo.instruction_count != instructions.size() ||
        memo.graph != &graph) {
        memo.instructions_data = instructions.data();
        memo.instruction_count = instructions.size();
        memo.graph = &graph;
        memo.cache.clear();
    }

    const LuaConditionalOwnershipCacheKey key{
        .conditional_line = conditional_line,
        .then_start = then_start,
        .branch_target = branch_target,
        .end_line = end_line,
        .loop_continue_target = loop_continue_target,
        .loop_exit_target = loop_exit_target,
        .consumed_span_start = consumed_span_start,
        .consumed_span_end = consumed_span_end,
    };
    const auto cached = memo.cache.find(key);
    if (cached != memo.cache.end()) {
        *ownership = cached->second;
        return true;
    }

    LuaConditionalOwnership classified;
    const bool ok = ClassifyLuaConditionalOwnershipCore(graph,
                                                        instructions,
                                                        conditional_line,
                                                        then_start,
                                                        branch_target,
                                                        end_line,
                                                        loop_continue_target,
                                                        loop_exit_target,
                                                        consumed_span_start,
                                                        consumed_span_end,
                                                        &classified);
    memo.cache.emplace(key, classified);
    *ownership = classified;
    return ok;
}

bool TryBuildLuaConditionalBlock(const Lua51ControlFlowGraph& graph,
                                 const std::vector<Lua51Instruction>& instructions,
                                 int conditional_line,
                                 int then_start,
                                 int branch_target,
                                 int end_line,
                                 int loop_continue_target,
                                 int loop_exit_target,
                                 LuaConditionalBlock* block) {
    if (block == nullptr || branch_target < 1 || then_start < 1) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    block->then_start = then_start;
    block->then_end = branch_target - 1;
    block->else_start = -1;
    block->else_end = -1;
    block->after_line = branch_target;
    block->shared_continue_target = -1;
    block->has_else = false;
    block->then_exits_loop = false;
    block->prefer_elseif_chain = false;
    LuaConditionalOwnership ownership;
    const bool have_ownership = TryClassifyLuaConditionalOwnership(graph,
                                                                   instructions,
                                                                   conditional_line,
                                                                   then_start,
                                                                   branch_target,
                                                                   end_line,
                                                                   loop_continue_target,
                                                                   loop_exit_target,
                                                                   &ownership);
    const bool ownership_allow_else = !have_ownership || ownership.allow_else;
    const bool ownership_allow_elseif =
        have_ownership && ownership.classified && ownership.allow_elseif;
    if (have_ownership && ownership.early_bailout) {
        block->then_end = std::min(end_line, branch_target - 1);
        block->else_start = -1;
        block->else_end = -1;
        block->after_line = branch_target;
        block->has_else = false;
        block->prefer_elseif_chain = false;
        return true;
    }

    if (branch_target - 1 < then_start ||
        branch_target - 1 > end_line ||
        branch_target - 1 > instruction_count) {
        if (branch_target - 1 == end_line + 1 &&
            end_line >= then_start &&
            static_cast<std::size_t>(branch_target - 1) <= instructions.size()) {
            const Lua51Instruction& trailing_outer_jump =
                instructions[static_cast<std::size_t>(branch_target - 2)];
            if (trailing_outer_jump.opcode == Lua51Opcode::kJmp) {
                const int trailing_outer_target = ComputeJumpTargetLine(
                    branch_target - 1, trailing_outer_jump.sbx, instruction_count);
                const int resolved_trailing_outer_target =
                    ResolveJumpTarget(instructions, trailing_outer_target);
                if (trailing_outer_target > branch_target ||
                    resolved_trailing_outer_target > branch_target ||
                    (loop_continue_target != -1 &&
                     (trailing_outer_target == loop_continue_target ||
                      resolved_trailing_outer_target == loop_continue_target)) ||
                    (loop_exit_target != -1 &&
                     (trailing_outer_target == loop_exit_target ||
                      resolved_trailing_outer_target == loop_exit_target))) {
                    // Nested conditionals inside a parent then-range can end one instruction
                    // before an enclosing tail jump that skips a shared outer else/tail body.
                    // Keep the inner block scoped to the proven current range and hand control
                    // back at the branch target so the enclosing range owns that tail jump.
                    block->then_end = end_line;
                    block->after_line = branch_target;
                }
            }
        }
        return true;
    }

    const Lua51Instruction& maybe_else_jump =
        instructions[static_cast<std::size_t>(branch_target - 2)];
    const int normalized_tail_end =
        end_line >= 1 &&
                static_cast<std::size_t>(end_line) <= instructions.size() &&
                instructions[static_cast<std::size_t>(end_line - 1)].opcode ==
                    Lua51Opcode::kReturn
            ? end_line - 1
            : end_line;
    if (branch_target >= 1 &&
        static_cast<std::size_t>(branch_target) <= instructions.size()) {
        const Lua51Instruction& else_leading =
            instructions[static_cast<std::size_t>(branch_target - 1)];
        if (else_leading.opcode == Lua51Opcode::kJmp) {
            const int else_leading_target =
                ComputeJumpTargetLine(branch_target, else_leading.sbx, instruction_count);
            const int resolved_else_leading_target =
                ResolveJumpTarget(instructions, else_leading_target);
            const int effective_else_leading_target =
                (resolved_else_leading_target > branch_target &&
                 resolved_else_leading_target <= end_line + 1)
                    ? resolved_else_leading_target
                    : else_leading_target;
            if (effective_else_leading_target > branch_target &&
                effective_else_leading_target <= end_line + 1) {
                block->then_end = branch_target - 2;
                block->else_start = -1;
                block->else_end = -1;
                block->after_line = effective_else_leading_target;
                block->has_else = false;
                return true;
            }
        }
    }
    const int trailing_then_jump_line = branch_target - 1;
    const auto extend_else_after_line_for_external_loop_back = [&]() {
        if (!block->has_else || block->else_start < 1 || block->else_end < block->else_start) {
            return;
        }

        const int external_else_jump_line = block->else_end + 1;
        if (external_else_jump_line < 1 ||
            static_cast<std::size_t>(external_else_jump_line) > instructions.size()) {
            return;
        }

        const Lua51Instruction& external_else_jump =
            instructions[static_cast<std::size_t>(external_else_jump_line - 1)];
        if (external_else_jump.opcode != Lua51Opcode::kJmp) {
            return;
        }

        const int external_else_target = ComputeJumpTargetLine(
            external_else_jump_line, external_else_jump.sbx, instruction_count);
        const int resolved_external_else_target =
            ResolveJumpTarget(instructions, external_else_target);
        const bool jumps_to_else_start =
            external_else_target == block->else_start ||
            resolved_external_else_target == block->else_start;
        const bool jumps_to_loop_continue =
            loop_continue_target != -1 &&
            (external_else_target == loop_continue_target ||
             resolved_external_else_target == loop_continue_target);
        if (jumps_to_else_start || jumps_to_loop_continue) {
            block->after_line = external_else_jump_line + 1;
        }
    };
    const auto try_build_loop_continue_else_tail = [&]() {
        if (loop_continue_target == -1 ||
            branch_target > normalized_tail_end ||
            trailing_then_jump_line < then_start ||
            trailing_then_jump_line < 1 ||
            static_cast<std::size_t>(trailing_then_jump_line) > instructions.size()) {
            return false;
        }

        const Lua51Instruction& trailing_then_jump =
            instructions[static_cast<std::size_t>(trailing_then_jump_line - 1)];
        if (trailing_then_jump.opcode != Lua51Opcode::kJmp) {
            return false;
        }

        const int trailing_then_target = ComputeJumpTargetLine(
            trailing_then_jump_line, trailing_then_jump.sbx, instruction_count);
        const int resolved_trailing_then_target =
            ResolveJumpTarget(instructions, trailing_then_target);
        if (trailing_then_target != loop_continue_target &&
            resolved_trailing_then_target != loop_continue_target) {
            return false;
        }

        block->then_end = trailing_then_jump_line - 1;
        block->else_start = branch_target;
        block->else_end = normalized_tail_end;
        block->after_line = normalized_tail_end + 1;
        block->has_else = true;
        block->then_exits_loop = true;
        extend_else_after_line_for_external_loop_back();
        return true;
    };
    const auto try_build_loop_break_then_tail = [&]() {
        if (loop_exit_target == -1 || branch_target != end_line + 1 ||
            trailing_then_jump_line < then_start || trailing_then_jump_line < 1 ||
            static_cast<std::size_t>(trailing_then_jump_line) > instructions.size()) {
            return false;
        }

        const Lua51Instruction& trailing_then_jump =
            instructions[static_cast<std::size_t>(trailing_then_jump_line - 1)];
        if (trailing_then_jump.opcode != Lua51Opcode::kJmp) {
            return false;
        }

        const int trailing_then_target = ComputeJumpTargetLine(
            trailing_then_jump_line, trailing_then_jump.sbx, instruction_count);
        const int resolved_trailing_then_target =
            ResolveJumpTarget(instructions, trailing_then_target);
        if (trailing_then_target != loop_exit_target &&
            resolved_trailing_then_target != loop_exit_target) {
            return false;
        }

        block->then_end = trailing_then_jump_line - 1;
        block->else_start = -1;
        block->else_end = -1;
        block->after_line = branch_target;
        block->has_else = false;
        block->then_exits_loop = true;
        return true;
    };
    if (maybe_else_jump.opcode != Lua51Opcode::kJmp) {
        if (try_build_loop_break_then_tail()) {
            return true;
        }
        if (try_build_loop_continue_else_tail()) {
            return true;
        }
        return true;
    }

    const int else_target =
        ComputeJumpTargetLine(branch_target - 1, maybe_else_jump.sbx, instruction_count);
    const int resolved_else_target =
        ResolveJumpTarget(instructions, else_target);
    const int effective_else_target =
        (resolved_else_target >= branch_target &&
         resolved_else_target <= end_line + 1)
            ? resolved_else_target
            : else_target;

    const bool tail_jump_belongs_to_current_range_tail =
        trailing_then_jump_line == end_line ||
        trailing_then_jump_line == end_line + 1;
    const auto is_unambiguous_shared_join = [&](int shared_join) {
        if (ownership_allow_elseif && ownership.effective_join_line > branch_target &&
            shared_join != ownership.effective_join_line) {
            return false;
        }
        if (have_ownership && ownership.classified && !ownership.allow_elseif) {
            return false;
        }
        return shared_join > branch_target &&
               !HasIncomingEdgeIntoRange(graph,
                                         branch_target,
                                         shared_join - 1,
                                         conditional_line,
                                         trailing_then_jump_line);
    };
    const bool has_external_owner_for_else_tail =
        effective_else_target > branch_target &&
        HasIncomingEdgeIntoRange(graph,
                                 branch_target,
                                 effective_else_target - 1,
                                 conditional_line,
                                 trailing_then_jump_line);
    if (effective_else_target > branch_target &&
        has_external_owner_for_else_tail &&
        tail_jump_belongs_to_current_range_tail &&
        trailing_then_jump_line >= then_start &&
        trailing_then_jump_line >= 1 &&
        static_cast<std::size_t>(trailing_then_jump_line) <= instructions.size()) {
        // Some nested conditionals end exactly one instruction before the tail jump that closes
        // their then-arm, or with that tail jump still included as the current subrange tail. In
        // both shapes the jump belongs to the enclosing else lane, not to the current inner
        // block. Keep the inner block scoped to its proven then-body and hand control back to the
        // enclosing range at the else entry instead of rendering the raw tail jump.
        block->then_end = branch_target - 2;
        block->else_start = -1;
        block->else_end = -1;
        block->after_line = branch_target;
        block->has_else = false;
        return true;
    }

    if (effective_else_target == branch_target) {
        // unluac-style join shaping: when the "else" lane immediately starts with
        // another conditional that reconverges with this block, model it as a real
        // else-range so branch rendering can emit a cleaner elseif/else structure
        // instead of punting to late cleanup/error-marker repair paths.
        const int else_join_floor =
            std::max(ComputeLuaConditionalElseJoinFloor(
                         instructions, branch_target, end_line, instruction_count),
                     ComputeLuaStatementJoinFloor(instructions, branch_target));
        const int join_search_end =
            std::min(end_line + 1, instruction_count);
        if (else_join_floor <= join_search_end) {
            const int shared_join =
                FindSharedJoinLine(graph,
                                   then_start,
                                   branch_target,
                                   else_join_floor,
                                   join_search_end);
            if (is_unambiguous_shared_join(shared_join)) {
                block->then_end = branch_target - 1;
                block->else_start = branch_target;
                block->else_end = shared_join - 1;
                block->after_line = shared_join;
                block->has_else = true;
                block->prefer_elseif_chain = ownership_allow_elseif;
                return true;
            }
        }

        // Plain `if ... then ... end` in Lua 5.1 uses the branch jump target as the
        // first instruction after the then-body. Keep the final body instruction
        // inside the then-range; otherwise single-line default setters and trailing
        // loop `break`s get stranded after a prematurely closed block.
        block->then_end = branch_target - 1;
        block->else_start = -1;
        block->else_end = -1;
        block->after_line = branch_target;
        block->has_else = false;
        return true;
    }

    if (effective_else_target > branch_target &&
        effective_else_target <= end_line + 1) {
        if (!ownership_allow_else) {
            block->then_end = branch_target - 2;
            block->else_start = -1;
            block->else_end = -1;
            block->after_line = branch_target;
            block->has_else = false;
            return true;
        }
        block->then_end = branch_target - 2;
        block->else_start = branch_target;
        block->else_end = effective_else_target - 1;
        block->after_line = effective_else_target;
        block->has_else = true;
        extend_else_after_line_for_external_loop_back();
        return true;
    }

    if (effective_else_target > branch_target + 0) {
        const int else_join_floor =
            std::max(ComputeLuaConditionalElseJoinFloor(
                         instructions, branch_target, end_line, instruction_count),
                     ComputeLuaStatementJoinFloor(instructions, branch_target));
        const int join_search_end = std::min(end_line + 1,
                                             std::max(effective_else_target, branch_target + 1));
        if (else_join_floor <= join_search_end) {
            const int shared_join =
                FindSharedJoinLine(graph,
                                   then_start,
                                   branch_target,
                                   else_join_floor,
                                   join_search_end);
            if (is_unambiguous_shared_join(shared_join)) {
                block->then_end = branch_target - 2;
                block->else_start = branch_target;
                block->else_end = shared_join - 1;
                block->after_line = shared_join;
                block->has_else = true;
                block->prefer_elseif_chain = ownership_allow_elseif;
                extend_else_after_line_for_external_loop_back();
                return true;
            }
        }
    }
    if (else_target < conditional_line &&
        branch_target <= end_line &&
        static_cast<std::size_t>(end_line) <= instructions.size()) {
        const Lua51Instruction& trailing_else_jump =
            instructions[static_cast<std::size_t>(end_line - 1)];
        if (trailing_else_jump.opcode == Lua51Opcode::kJmp &&
            ComputeJumpTargetLine(end_line, trailing_else_jump.sbx, instruction_count) ==
                else_target) {
            block->then_end = branch_target - 2;
            block->else_start = branch_target;
            block->else_end = end_line;
            block->after_line = end_line + 1;
            block->shared_continue_target = else_target;
            block->has_else = true;
            extend_else_after_line_for_external_loop_back();
            return true;
        }
    }
    const bool ends_current_range =
        effective_else_target > branch_target && effective_else_target <= end_line + 1;
    const bool exits_current_loop =
        loop_exit_target != -1 &&
        (else_target == loop_exit_target || resolved_else_target == loop_exit_target);
    const bool reaches_shared_loop_tail =
        loop_exit_target != -1 &&
        ((else_target > end_line + 1 && else_target > branch_target &&
          else_target < loop_exit_target) ||
         (resolved_else_target > end_line + 1 && resolved_else_target > branch_target &&
          resolved_else_target < loop_exit_target));
    if (effective_else_target > end_line + 1 &&
        branch_target <= end_line &&
        !exits_current_loop &&
        !reaches_shared_loop_tail) {
        block->then_end = branch_target - 2;
        block->else_start = branch_target;
        block->else_end = end_line;
        block->after_line = effective_else_target;
        block->has_else = true;
        extend_else_after_line_for_external_loop_back();
        return true;
    }
    if (!ends_current_range && !exits_current_loop && !reaches_shared_loop_tail) {
        const int else_join_floor =
            std::max(ComputeLuaConditionalElseJoinFloor(
                         instructions, branch_target, end_line, instruction_count),
                     ComputeLuaStatementJoinFloor(instructions, branch_target));
        const int join_search_end =
            std::min(end_line + 1, instruction_count);
        if (else_join_floor <= join_search_end) {
            const int shared_join =
                FindSharedJoinLine(graph,
                                   then_start,
                                   branch_target,
                                   else_join_floor,
                                   join_search_end);
            if (is_unambiguous_shared_join(shared_join)) {
                block->then_end = branch_target - 2;
                block->else_start = branch_target;
                block->else_end = shared_join - 1;
                block->after_line = shared_join;
                block->has_else = true;
                block->prefer_elseif_chain = ownership_allow_elseif;
                extend_else_after_line_for_external_loop_back();
                return true;
            }
        }
        if (try_build_loop_continue_else_tail()) {
            return true;
        }
        return true;
    }

    block->then_end = branch_target - 2;
    const int candidate_else_end =
        (ends_current_range || reaches_shared_loop_tail) ? effective_else_target - 1 : end_line;
    const bool shared_join_target =
        candidate_else_end >= branch_target &&
        HasIncomingEdgeIntoRange(graph,
                                 branch_target,
                                 candidate_else_end,
                                 conditional_line,
                                 std::max(conditional_line, then_start - 1));
    if (shared_join_target || !ownership_allow_else) {
        block->after_line = branch_target;
        return true;
    }

    block->else_start = branch_target;
    block->else_end = candidate_else_end;
    block->after_line = candidate_else_end + 1;
    block->has_else = true;
    block->then_exits_loop = exits_current_loop;
    extend_else_after_line_for_external_loop_back();
    return true;
}

bool TryBuildLuaSharedTailConditionalBlock(const std::vector<Lua51Instruction>& instructions,
                                           int branch_target,
                                           int combined_then_start,
                                           int end_line,
                                           int loop_continue_target,
                                           LuaSharedTailConditionalBlock* block) {
    if (block == nullptr) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    if (branch_target <= combined_then_start + 1 ||
        branch_target - 1 > instruction_count ||
        branch_target < 1) {
        return false;
    }

    const int inner_condition_line = branch_target - 2;
    const int inner_jump_line = branch_target - 1;
    if (inner_condition_line < combined_then_start ||
        inner_condition_line < 1 ||
        inner_jump_line < 1) {
        return false;
    }

    const Lua51Instruction& inner_condition_instruction =
        instructions[static_cast<std::size_t>(inner_condition_line - 1)];
    const Lua51Instruction& inner_jump_instruction =
        instructions[static_cast<std::size_t>(inner_jump_line - 1)];
    if (!IsConditionalBranchOpcode(inner_condition_instruction.opcode) ||
        inner_jump_instruction.opcode != Lua51Opcode::kJmp) {
        return false;
    }

    const int raw_shared_exit =
        ComputeJumpTargetLine(inner_jump_line, inner_jump_instruction.sbx, instruction_count);
    const int resolved_shared_exit =
        ResolveJumpTarget(instructions, raw_shared_exit);
    const int shared_exit =
        (resolved_shared_exit > branch_target && resolved_shared_exit <= end_line + 1)
            ? resolved_shared_exit
            : raw_shared_exit;
    if (shared_exit <= branch_target || shared_exit > end_line + 1) {
        return false;
    }

    int common_exit = -1;
    bool ambiguous_common_exit = false;
    bool saw_jump_past_shared_exit = false;
    bool saw_loop_continue_jump = false;
    for (int candidate_line = branch_target;
         candidate_line <= shared_exit - 1 &&
         static_cast<std::size_t>(candidate_line) <= instructions.size();
         ++candidate_line) {
        const Lua51Instruction& candidate_instruction =
            instructions[static_cast<std::size_t>(candidate_line - 1)];
        if (candidate_instruction.opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const int candidate_target =
            ComputeJumpTargetLine(candidate_line, candidate_instruction.sbx, instruction_count);
        const int resolved_candidate_target =
            ResolveJumpTarget(instructions, candidate_target);
        const int effective_candidate_target =
            (resolved_candidate_target > shared_exit &&
             resolved_candidate_target <= end_line + 1)
                ? resolved_candidate_target
                : candidate_target;
        if (loop_continue_target != -1 &&
            (candidate_target == loop_continue_target ||
             resolved_candidate_target == loop_continue_target)) {
            saw_loop_continue_jump = true;
        }
        if (effective_candidate_target <= shared_exit) {
            continue;
        }

        saw_jump_past_shared_exit = true;
        if (common_exit == -1) {
            common_exit = effective_candidate_target;
        } else if (common_exit != effective_candidate_target) {
            ambiguous_common_exit = true;
            break;
        }
    }

    if (ambiguous_common_exit) {
        return false;
    }

    block->inner_condition_line = inner_condition_line;
    block->inner_jump_line = inner_jump_line;
    block->shared_exit = shared_exit;
    block->then_start = branch_target;
    block->then_end = shared_exit - 1;
    block->else_start = -1;
    block->else_end = -1;
    block->after_line = shared_exit;
    block->has_else = false;

    if (saw_jump_past_shared_exit && common_exit > shared_exit && common_exit <= end_line + 1) {
        block->else_start = shared_exit;
        block->else_end = common_exit - 1;
        block->after_line = common_exit;
        block->has_else = true;
        return true;
    }

    const int normalized_tail_end =
        end_line >= 1 &&
                static_cast<std::size_t>(end_line) <= instructions.size() &&
                instructions[static_cast<std::size_t>(end_line - 1)].opcode ==
                    Lua51Opcode::kReturn
            ? end_line - 1
            : end_line;
    if (!block->has_else && loop_continue_target != -1 && saw_loop_continue_jump &&
        shared_exit <= normalized_tail_end) {
        block->else_start = shared_exit;
        block->else_end = normalized_tail_end;
        block->after_line = normalized_tail_end + 1;
        block->has_else = true;
    }

    return true;
}

bool TryBuildLuaBackJumpLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                  int current,
                                  int branch_target,
                                  int end_line,
                                  LuaBackJumpLoopBlock* block) {
    if (block == nullptr) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    if (branch_target - 1 < current + 2 ||
        branch_target - 1 > end_line ||
        branch_target - 1 > instruction_count) {
        return false;
    }

    const Lua51Instruction& maybe_loop =
        instructions[static_cast<std::size_t>(branch_target - 2)];
    if (maybe_loop.opcode != Lua51Opcode::kJmp ||
        ComputeJumpTargetLine(branch_target - 1, maybe_loop.sbx, instruction_count) != current) {
        return false;
    }

    block->body_start = current + 2;
    block->body_end = branch_target - 2;
    block->continue_target = current;
    block->exit_target = branch_target;
    block->after_line = branch_target;
    return true;
}

bool TryBuildLuaGenericForLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                    int current,
                                    LuaGenericForLoopBlock* block) {
    if (block == nullptr || current < 1 ||
        static_cast<std::size_t>(current) > instructions.size()) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    const Lua51Instruction& instruction =
        instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kCall || current + 2 > instruction_count ||
        instructions[static_cast<std::size_t>(current)].opcode != Lua51Opcode::kJmp) {
        return false;
    }

    const Lua51Instruction& pre_loop_jump =
        instructions[static_cast<std::size_t>(current)];
    const int loop_check_line =
        ComputeJumpTarget(current + 1, pre_loop_jump.sbx, instruction_count);
    if (loop_check_line < current + 2 || loop_check_line + 1 > instruction_count) {
        return false;
    }

    const Lua51Instruction& tfor =
        instructions[static_cast<std::size_t>(loop_check_line - 1)];
    const Lua51Instruction& loop_back =
        instructions[static_cast<std::size_t>(loop_check_line)];
    const int body_start = current + 2;
    const int loop_exit = loop_check_line + 2;
    if (tfor.opcode != Lua51Opcode::kTForLoop || tfor.a != instruction.a ||
        loop_back.opcode != Lua51Opcode::kJmp ||
        ComputeJumpTarget(loop_check_line + 1, loop_back.sbx, instruction_count) != body_start) {
        return false;
    }

    block->body_start = body_start;
    block->body_end = loop_check_line - 1;
    block->loop_check_line = loop_check_line;
    block->loop_exit = loop_exit;
    block->base_register = instruction.a;
    block->first_loop_register = instruction.a + 3;
    block->last_loop_register = instruction.a + 2 + tfor.c;
    return true;
}

bool TryBuildLuaNumericForLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                    int current,
                                    LuaNumericForLoopBlock* block) {
    if (block == nullptr || current < 1 ||
        static_cast<std::size_t>(current) > instructions.size()) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    const Lua51Instruction& instruction =
        instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kForPrep) {
        return false;
    }

    const int forloop_line =
        ComputeJumpTarget(current, instruction.sbx, instruction_count);
    if (forloop_line < current + 1 || forloop_line > instruction_count) {
        return false;
    }

    const Lua51Instruction& forloop =
        instructions[static_cast<std::size_t>(forloop_line - 1)];
    if (forloop.opcode != Lua51Opcode::kForLoop || forloop.a != instruction.a) {
        return false;
    }

    block->body_start = current + 1;
    block->body_end = forloop_line - 1;
    block->loop_exit = forloop_line + 1;
    block->forloop_line = forloop_line;
    block->base_register = instruction.a;
    block->loop_var_register = instruction.a + 3;
    return true;
}

bool TryBuildLuaRepeatLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                int current,
                                int end_line,
                                LuaRepeatLoopBlock* block) {
    if (block == nullptr || current < 1 ||
        static_cast<std::size_t>(current) > instructions.size()) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    for (int condition_line = current + 1; condition_line <= end_line - 1; ++condition_line) {
        const Lua51Instruction& repeat_condition =
            instructions[static_cast<std::size_t>(condition_line - 1)];
        const Lua51Instruction& repeat_jump =
            instructions[static_cast<std::size_t>(condition_line)];
        if (!IsConditionalBranchOpcode(repeat_condition.opcode) ||
            repeat_jump.opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const int repeat_target =
            ComputeJumpTarget(condition_line + 1, repeat_jump.sbx, instruction_count);
        const int resolved_repeat_target = ResolveJumpTarget(instructions, repeat_target);
        if (repeat_target != current && resolved_repeat_target != current) {
            continue;
        }

        block->body_start = current;
        block->body_end = condition_line - 1;
        block->condition_line = condition_line;
        block->after_line = condition_line + 2;
        return true;
    }

    return false;
}

bool TryBuildLuaRangeEntryLoopBlock(const std::vector<Lua51Instruction>& instructions,
                                    int start_line,
                                    int end_line,
                                    int loop_continue_target,
                                    int loop_exit_target,
                                    LuaRangeEntryLoopBlock* block) {
    if (block == nullptr || start_line < 3 ||
        static_cast<std::size_t>(start_line - 1) > instructions.size()) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    const int condition_line = start_line - 2;
    const int guard_jump_line = start_line - 1;
    if (condition_line == loop_continue_target ||
        static_cast<std::size_t>(guard_jump_line) > instructions.size()) {
        return false;
    }

    const Lua51Instruction& start_condition =
        instructions[static_cast<std::size_t>(condition_line - 1)];
    const Lua51Instruction& start_guard_jump =
        instructions[static_cast<std::size_t>(guard_jump_line - 1)];
    if (!IsConditionalBranchOpcode(start_condition.opcode) ||
        start_guard_jump.opcode != Lua51Opcode::kJmp) {
        return false;
    }

    const int guard_target =
        ComputeJumpTarget(guard_jump_line, start_guard_jump.sbx, instruction_count);
    const int resolved_guard_target = ResolveJumpTarget(instructions, guard_target);
    const bool ends_current_range =
        guard_target == end_line + 1 || resolved_guard_target == end_line + 1;
    const bool exits_parent =
        guard_target == loop_continue_target ||
        resolved_guard_target == loop_continue_target ||
        (loop_exit_target != -1 &&
         (guard_target == loop_exit_target || resolved_guard_target == loop_exit_target));
    if (!exits_parent && !ends_current_range) {
        return false;
    }

    block->body_start = start_line;
    block->body_end = -1;
    block->condition_line = condition_line;
    block->condition_render_line =
        static_cast<int>(start_condition.line == 0 ? condition_line : start_condition.line);
    block->guard_jump_line = guard_jump_line;
    block->guard_target = guard_target;
    block->resolved_guard_target = resolved_guard_target;
    block->continue_target = -1;
    block->exit_target = guard_target;
    block->preamble_start = -1;
    block->ends_current_range = ends_current_range;
    block->exits_parent = exits_parent;

    for (int candidate = end_line; candidate >= start_line; --candidate) {
        const Lua51Instruction& maybe_loop_back =
            instructions[static_cast<std::size_t>(candidate - 1)];
        if (maybe_loop_back.opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const int maybe_loop_target =
            ComputeJumpTarget(candidate, maybe_loop_back.sbx, instruction_count);
        if (maybe_loop_target == condition_line) {
            block->body_end = candidate - 1;
            block->continue_target = condition_line;
            return true;
        }
    }

    for (int candidate = end_line; candidate >= start_line; --candidate) {
        const Lua51Instruction& maybe_loop_back =
            instructions[static_cast<std::size_t>(candidate - 1)];
        if (maybe_loop_back.opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const int preamble_start =
            ComputeJumpTarget(candidate, maybe_loop_back.sbx, instruction_count);
        const int resolved_preamble_start =
            ResolveJumpTarget(instructions, preamble_start);
        if (preamble_start < 1 || preamble_start >= condition_line) {
            continue;
        }
        if (loop_continue_target != -1 &&
            (preamble_start == loop_continue_target ||
             resolved_preamble_start == loop_continue_target)) {
            continue;
        }

        block->body_end = candidate - 1;
        block->continue_target = preamble_start;
        block->preamble_start = preamble_start;
        return true;
    }

    return false;
}

bool TryBuildLuaChainedConditionLoopBlock(
    const std::vector<Lua51Instruction>& instructions,
    int current,
    int end_line,
    int loop_continue_target,
    int loop_exit_target,
    const std::function<bool(int segment_start, int condition_line)>& accepts_condition_line,
    LuaChainedConditionLoopBlock* block) {
    if (block == nullptr || current < 1 ||
        static_cast<std::size_t>(current) > instructions.size()) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    const auto is_guard_jump_payload = [&](int candidate_line) {
        return candidate_line > 1 &&
               static_cast<std::size_t>(candidate_line - 1) <= instructions.size() &&
               IsConditionalBranchOpcode(
                   instructions[static_cast<std::size_t>(candidate_line - 2)].opcode);
    };
    int loop_back_line = -1;
    for (int candidate = end_line; candidate >= current + 2; --candidate) {
        const Lua51Instruction& loop_back =
            instructions[static_cast<std::size_t>(candidate - 1)];
        if (loop_back.opcode != Lua51Opcode::kJmp ||
            ComputeJumpTarget(candidate, loop_back.sbx, instruction_count) != current ||
            is_guard_jump_payload(candidate)) {
            continue;
        }
        loop_back_line = candidate;
        break;
    }

    if (loop_back_line == -1) {
        const int external_loop_back_line = end_line + 1;
        if (external_loop_back_line >= current + 2 &&
            static_cast<std::size_t>(external_loop_back_line) <= instructions.size()) {
            const Lua51Instruction& external_loop_back =
                instructions[static_cast<std::size_t>(external_loop_back_line - 1)];
            if (external_loop_back.opcode == Lua51Opcode::kJmp &&
                !is_guard_jump_payload(external_loop_back_line) &&
                ComputeJumpTarget(external_loop_back_line,
                                  external_loop_back.sbx,
                                  instruction_count) == current) {
                loop_back_line = external_loop_back_line;
            }
        }
    }

    if (loop_back_line == -1) {
        return false;
    }

    const int default_loop_exit = loop_back_line + 1;
    int loop_exit = default_loop_exit;
    int segment_start = current;
    block->body_start = current;
    block->body_end = loop_back_line - 1;
    block->loop_back_line = loop_back_line;
    block->default_loop_exit = default_loop_exit;
    block->loop_exit = default_loop_exit;
    block->segment_starts.clear();
    block->condition_lines.clear();

    while (segment_start <= loop_back_line - 1) {
        bool matched_segment = false;
        for (int condition_line = segment_start; condition_line <= loop_back_line - 1;
             ++condition_line) {
            const Lua51Instruction& loop_condition =
                instructions[static_cast<std::size_t>(condition_line - 1)];
            if (!IsConditionalBranchOpcode(loop_condition.opcode) ||
                condition_line >= instruction_count) {
                continue;
            }

            const Lua51Instruction& loop_exit_jump =
                instructions[static_cast<std::size_t>(condition_line)];
            if (loop_exit_jump.opcode != Lua51Opcode::kJmp) {
                continue;
            }

            const int candidate_loop_exit =
                ComputeJumpTarget(condition_line + 1, loop_exit_jump.sbx, instruction_count);
            const int resolved_candidate_loop_exit =
                ResolveJumpTarget(instructions, candidate_loop_exit);
            const bool matches_loop_exit =
                candidate_loop_exit == default_loop_exit ||
                resolved_candidate_loop_exit == default_loop_exit ||
                (loop_continue_target != -1 &&
                 (candidate_loop_exit == loop_continue_target ||
                  resolved_candidate_loop_exit == loop_continue_target)) ||
                (loop_exit_target != -1 &&
                 (candidate_loop_exit == loop_exit_target ||
                  resolved_candidate_loop_exit == loop_exit_target));
            if (!matches_loop_exit) {
                continue;
            }

            if (accepts_condition_line &&
                !accepts_condition_line(segment_start, condition_line)) {
                continue;
            }

            block->segment_starts.push_back(segment_start);
            block->condition_lines.push_back(condition_line);
            loop_exit = resolved_candidate_loop_exit;
            segment_start = condition_line + 2;
            matched_segment = true;
            break;
        }

        if (!matched_segment) {
            break;
        }
    }

    block->body_start = segment_start;
    block->loop_exit = loop_exit;
    return true;
}

bool TryBuildLuaGuardedContinueBlock(const std::vector<Lua51Instruction>& instructions,
                                     int current,
                                     int end_line,
                                     int loop_continue_target,
                                     LuaGuardedContinueBlock* block) {
    if (block == nullptr || loop_continue_target == -1 ||
        current < 1 || static_cast<std::size_t>(current) > instructions.size()) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    for (int condition_line = current; condition_line <= end_line - 1; ++condition_line) {
        const Lua51Instruction& guard_condition =
            instructions[static_cast<std::size_t>(condition_line - 1)];
        const Lua51Instruction& guard_jump =
            instructions[static_cast<std::size_t>(condition_line)];
        if (!IsConditionalBranchOpcode(guard_condition.opcode) ||
            guard_jump.opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const int guard_target =
            ComputeJumpTarget(condition_line + 1, guard_jump.sbx, instruction_count);
        const int resolved_guard_target = ResolveJumpTarget(instructions, guard_target);
        if (guard_target != loop_continue_target &&
            resolved_guard_target != loop_continue_target) {
            continue;
        }

        block->condition_line = condition_line;
        block->condition_render_line =
            static_cast<int>(guard_condition.line == 0 ? condition_line
                                                       : guard_condition.line);
        block->body_start = condition_line + 2;
        block->body_end = end_line;
        block->guard_target = guard_target;
        block->resolved_guard_target = resolved_guard_target;
        return true;
    }

    return false;
}

bool TryBuildLuaTestSetLoopControlBlock(const std::vector<Lua51Instruction>& instructions,
                                        int current,
                                        LuaTestSetLoopControlBlock* block) {
    if (block == nullptr || current < 1 ||
        static_cast<std::size_t>(current) > instructions.size()) {
        return false;
    }

    const int instruction_count = static_cast<int>(instructions.size());
    const Lua51Instruction& instruction =
        instructions[static_cast<std::size_t>(current - 1)];
    if (instruction.opcode != Lua51Opcode::kTestSet ||
        current + 1 > instruction_count ||
        instructions[static_cast<std::size_t>(current)].opcode != Lua51Opcode::kJmp) {
        return false;
    }

    const Lua51Instruction& jump = instructions[static_cast<std::size_t>(current)];
    const int branch_target =
        ComputeJumpTarget(current + 1, jump.sbx, instruction_count);
    if (branch_target < current + 2 || branch_target > instruction_count + 1) {
        return false;
    }

    block->branch_target = branch_target;
    block->fallback_start = current + 2;
    block->fallback_end = branch_target - 1;
    block->target_register = instruction.a;
    block->source_register = instruction.b;
    block->invert_condition = instruction.c != 0;
    return true;
}

}  // namespace refii::lua::decompiler
