#include "tools/decompiler/control_flow_blocks.h"

namespace refii::lua::decompiler {

namespace {

int ComputeJumpTarget(int pc, int sbx, int instruction_count) {
    return ComputeJumpTargetLine(pc, sbx, instruction_count);
}

int ResolveJumpTarget(const std::vector<Lua51Instruction>& instructions, int target_line) {
    return ResolveTerminalJumpTarget(instructions, target_line);
}

}  // namespace

bool TryBuildLuaConditionalBlock(const Lua51ControlFlowGraph& graph,
                                 const std::vector<Lua51Instruction>& instructions,
                                 int conditional_line,
                                 int then_start,
                                 int branch_target,
                                 int end_line,
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

    if (branch_target - 1 < then_start ||
        branch_target - 1 > end_line ||
        branch_target - 1 > instruction_count) {
        return true;
    }

    const Lua51Instruction& maybe_else_jump =
        instructions[static_cast<std::size_t>(branch_target - 2)];
    if (maybe_else_jump.opcode != Lua51Opcode::kJmp) {
        return true;
    }

    const int else_target =
        ComputeJumpTargetLine(branch_target - 1, maybe_else_jump.sbx, instruction_count);
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
            return true;
        }
    }
    const bool ends_current_range =
        else_target > branch_target && else_target <= end_line + 1;
    const bool exits_current_loop =
        loop_exit_target != -1 && else_target == loop_exit_target;
    const bool reaches_shared_loop_tail =
        loop_exit_target != -1 && else_target > end_line + 1 &&
        else_target > branch_target && else_target < loop_exit_target;
    if (!ends_current_range && !exits_current_loop && !reaches_shared_loop_tail) {
        return true;
    }

    block->then_end = branch_target - 2;
    const int candidate_else_end =
        (ends_current_range || reaches_shared_loop_tail) ? else_target - 1 : end_line;
    const bool shared_join_target =
        candidate_else_end >= branch_target &&
        HasIncomingEdgeIntoRange(graph, branch_target, candidate_else_end, conditional_line);
    if (shared_join_target) {
        block->after_line = branch_target;
        return true;
    }

    block->else_start = branch_target;
    block->else_end = candidate_else_end;
    block->after_line = candidate_else_end + 1;
    block->has_else = true;
    block->then_exits_loop = exits_current_loop;
    return true;
}

bool TryBuildLuaSharedTailConditionalBlock(const std::vector<Lua51Instruction>& instructions,
                                           int branch_target,
                                           int combined_then_start,
                                           int end_line,
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

    const int shared_exit =
        ComputeJumpTargetLine(inner_jump_line, inner_jump_instruction.sbx, instruction_count);
    if (shared_exit <= branch_target || shared_exit > end_line + 1) {
        return false;
    }

    int common_exit = -1;
    bool ambiguous_common_exit = false;
    bool saw_jump_past_shared_exit = false;
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
        if (candidate_target <= shared_exit) {
            continue;
        }

        saw_jump_past_shared_exit = true;
        if (common_exit == -1) {
            common_exit = candidate_target;
        } else if (common_exit != candidate_target) {
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
        if (preamble_start < 1 || preamble_start >= condition_line) {
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
    int loop_back_line = -1;
    for (int candidate = end_line; candidate >= current + 2; --candidate) {
        const Lua51Instruction& loop_back =
            instructions[static_cast<std::size_t>(candidate - 1)];
        if (loop_back.opcode != Lua51Opcode::kJmp ||
            ComputeJumpTarget(candidate, loop_back.sbx, instruction_count) != current) {
            continue;
        }
        loop_back_line = candidate;
        break;
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
