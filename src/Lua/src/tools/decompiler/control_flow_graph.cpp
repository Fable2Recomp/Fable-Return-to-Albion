#include "tools/decompiler/control_flow_graph.h"

#include <algorithm>
#include <queue>
#include <unordered_set>

namespace refii::lua::decompiler {
namespace {

int ClampTargetLine(int target, int instruction_count) {
    if (target < 1) {
        return 1;
    }
    if (target > instruction_count) {
        return instruction_count;
    }
    return target;
}

void AddUniqueInt(std::vector<int>* values, int value) {
    if (values == nullptr) {
        return;
    }
    if (std::find(values->begin(), values->end(), value) == values->end()) {
        values->push_back(value);
    }
}

void AddEdge(Lua51ControlFlowGraph* graph,
             int source_block,
             int target_block,
             int source_line) {
    if (graph == nullptr || source_block < 0 || target_block < 0 ||
        static_cast<std::size_t>(source_block) >= graph->blocks.size() ||
        static_cast<std::size_t>(target_block) >= graph->blocks.size()) {
        return;
    }

    for (const Lua51ControlFlowEdge& edge : graph->edges) {
        if (edge.source_block == source_block &&
            edge.target_block == target_block &&
            edge.source_line == source_line) {
            return;
        }
    }

    graph->edges.push_back(Lua51ControlFlowEdge{
        .source_block = source_block,
        .target_block = target_block,
        .source_line = source_line,
    });
    AddUniqueInt(&graph->blocks[static_cast<std::size_t>(source_block)].successors, target_block);
    AddUniqueInt(&graph->blocks[static_cast<std::size_t>(target_block)].predecessors, source_block);
}

int BlockIndexForLine(const Lua51ControlFlowGraph& graph, int line) {
    if (line < 1 || static_cast<std::size_t>(line) >= graph.line_to_block.size()) {
        return -1;
    }
    return graph.line_to_block[static_cast<std::size_t>(line)];
}

bool IsReachableAvoidingBlocks(const Lua51ControlFlowGraph& graph,
                               int start_block,
                               int target_block,
                               const std::unordered_set<int>& blocked_blocks) {
    if (start_block < 0 || target_block < 0 ||
        static_cast<std::size_t>(start_block) >= graph.blocks.size() ||
        static_cast<std::size_t>(target_block) >= graph.blocks.size()) {
        return false;
    }
    if (blocked_blocks.find(start_block) != blocked_blocks.end()) {
        return false;
    }
    if (start_block == target_block) {
        return true;
    }

    std::vector<bool> visited(graph.blocks.size(), false);
    std::queue<int> worklist;
    worklist.push(start_block);
    visited[static_cast<std::size_t>(start_block)] = true;

    while (!worklist.empty()) {
        const int block_index = worklist.front();
        worklist.pop();

        const Lua51BasicBlock& block =
            graph.blocks[static_cast<std::size_t>(block_index)];
        for (int successor_index : block.successors) {
            if (successor_index < 0 ||
                static_cast<std::size_t>(successor_index) >= graph.blocks.size() ||
                blocked_blocks.find(successor_index) != blocked_blocks.end()) {
                continue;
            }
            if (successor_index == target_block) {
                return true;
            }
            if (visited[static_cast<std::size_t>(successor_index)]) {
                continue;
            }
            visited[static_cast<std::size_t>(successor_index)] = true;
            worklist.push(successor_index);
        }
    }

    return false;
}

std::vector<bool> CollectReachableBlocks(const Lua51ControlFlowGraph& graph,
                                         int start_block,
                                         int max_join_line) {
    std::vector<bool> reachable(graph.blocks.size(), false);
    if (start_block < 0 ||
        static_cast<std::size_t>(start_block) >= graph.blocks.size()) {
        return reachable;
    }

    std::queue<int> worklist;
    worklist.push(start_block);
    reachable[static_cast<std::size_t>(start_block)] = true;

    while (!worklist.empty()) {
        const int block_index = worklist.front();
        worklist.pop();

        const Lua51BasicBlock& block =
            graph.blocks[static_cast<std::size_t>(block_index)];
        for (int successor_index : block.successors) {
            if (successor_index < 0 ||
                static_cast<std::size_t>(successor_index) >= graph.blocks.size()) {
                continue;
            }

            const Lua51BasicBlock& successor =
                graph.blocks[static_cast<std::size_t>(successor_index)];
            if (successor.start_line > max_join_line ||
                reachable[static_cast<std::size_t>(successor_index)]) {
                continue;
            }

            reachable[static_cast<std::size_t>(successor_index)] = true;
            worklist.push(successor_index);
        }
    }

    return reachable;
}

}  // namespace

bool IsConditionalBranchOpcode(Lua51Opcode opcode) {
    switch (opcode) {
        case Lua51Opcode::kEq:
        case Lua51Opcode::kLt:
        case Lua51Opcode::kLe:
        case Lua51Opcode::kTest:
        case Lua51Opcode::kTestSet:
            return true;
        default:
            return false;
    }
}

int ComputeJumpTargetLine(int pc, int sbx, int instruction_count) {
    return ClampTargetLine(pc + 1 + sbx, instruction_count);
}

int ResolveTerminalJumpTarget(const std::vector<Lua51Instruction>& instructions,
                              int target_line,
                              int max_depth) {
    int current = target_line;
    const int instruction_count = static_cast<int>(instructions.size());
    for (int depth = 0; depth < max_depth; ++depth) {
        if (current < 1 || current > instruction_count) {
            return current;
        }

        const Lua51Instruction& instruction =
            instructions[static_cast<std::size_t>(current - 1)];
        if (instruction.opcode == Lua51Opcode::kJmp) {
            const int next =
                ComputeJumpTargetLine(current, instruction.sbx, instruction_count);
            if (next == current) {
                return current;
            }
            current = next;
            continue;
        }

        if (instruction.opcode == Lua51Opcode::kClose && current < instruction_count) {
            const Lua51Instruction& next_instruction =
                instructions[static_cast<std::size_t>(current)];
            if (next_instruction.opcode == Lua51Opcode::kJmp) {
                current =
                    ComputeJumpTargetLine(current + 1, next_instruction.sbx, instruction_count);
                continue;
            }
        }

        return current;
    }

    return current;
}

Lua51ControlFlowGraph BuildLua51ControlFlowGraph(
    const std::vector<Lua51Instruction>& instructions) {
    Lua51ControlFlowGraph graph;
    const int instruction_count = static_cast<int>(instructions.size());
    graph.line_to_block.resize(static_cast<std::size_t>(instruction_count + 2), -1);
    if (instruction_count == 0) {
        return graph;
    }

    std::unordered_set<int> leaders;
    leaders.insert(1);

    for (int pc = 1; pc <= instruction_count; ++pc) {
        const Lua51Instruction& instruction =
            instructions[static_cast<std::size_t>(pc - 1)];

        if (instruction.opcode == Lua51Opcode::kJmp) {
            leaders.insert(ComputeJumpTargetLine(pc, instruction.sbx, instruction_count));
            if (pc + 1 <= instruction_count) {
                leaders.insert(pc + 1);
            }
            continue;
        }

        if (IsConditionalBranchOpcode(instruction.opcode) &&
            pc < instruction_count &&
            instructions[static_cast<std::size_t>(pc)].opcode == Lua51Opcode::kJmp) {
            leaders.insert(pc);
            leaders.insert(ComputeJumpTargetLine(pc + 1,
                                                 instructions[static_cast<std::size_t>(pc)].sbx,
                                                 instruction_count));
            if (pc + 2 <= instruction_count) {
                leaders.insert(pc + 2);
            }
            continue;
        }

        if (instruction.opcode == Lua51Opcode::kForPrep ||
            instruction.opcode == Lua51Opcode::kForLoop) {
            leaders.insert(pc);
            leaders.insert(ComputeJumpTargetLine(pc, instruction.sbx, instruction_count));
            if (pc + 1 <= instruction_count) {
                leaders.insert(pc + 1);
            }
            continue;
        }

        if (instruction.opcode == Lua51Opcode::kTForLoop) {
            leaders.insert(pc);
            if (pc + 1 <= instruction_count) {
                leaders.insert(pc + 1);
            }
            if (pc + 2 <= instruction_count) {
                leaders.insert(pc + 2);
            }
            continue;
        }

        if ((instruction.opcode == Lua51Opcode::kReturn ||
             instruction.opcode == Lua51Opcode::kTailCall) &&
            pc + 1 <= instruction_count) {
            leaders.insert(pc + 1);
        }
    }

    std::vector<int> sorted_leaders(leaders.begin(), leaders.end());
    std::sort(sorted_leaders.begin(), sorted_leaders.end());
    sorted_leaders.erase(std::remove_if(sorted_leaders.begin(),
                                        sorted_leaders.end(),
                                        [&](int line) {
                                            return line < 1 || line > instruction_count;
                                        }),
                         sorted_leaders.end());

    for (std::size_t index = 0; index < sorted_leaders.size(); ++index) {
        const int start_line = sorted_leaders[index];
        const int end_line =
            index + 1 < sorted_leaders.size() ? sorted_leaders[index + 1] - 1
                                              : instruction_count;
        Lua51BasicBlock block;
        block.index = static_cast<int>(index);
        block.start_line = start_line;
        block.end_line = end_line;
        if (end_line >= start_line && end_line >= 1 && end_line <= instruction_count) {
            const Lua51Instruction& tail =
                instructions[static_cast<std::size_t>(end_line - 1)];
            const bool ends_in_conditional_pair =
                end_line > start_line &&
                IsConditionalBranchOpcode(
                    instructions[static_cast<std::size_t>(end_line - 2)].opcode) &&
                tail.opcode == Lua51Opcode::kJmp;
            block.ends_in_conditional =
                IsConditionalBranchOpcode(tail.opcode) || ends_in_conditional_pair;
            block.ends_in_unconditional_jump =
                tail.opcode == Lua51Opcode::kJmp && !ends_in_conditional_pair;
        }
        graph.blocks.push_back(std::move(block));
        for (int line = start_line; line <= end_line; ++line) {
            graph.line_to_block[static_cast<std::size_t>(line)] = static_cast<int>(index);
        }
    }

    for (Lua51BasicBlock& block : graph.blocks) {
        if (block.end_line < 1 || block.end_line > instruction_count) {
            continue;
        }

        const Lua51Instruction& tail =
            instructions[static_cast<std::size_t>(block.end_line - 1)];
        const int block_index = block.index;

        const bool ends_in_conditional_pair =
            block.end_line > block.start_line &&
            IsConditionalBranchOpcode(
                instructions[static_cast<std::size_t>(block.end_line - 2)].opcode) &&
            tail.opcode == Lua51Opcode::kJmp;

        if (ends_in_conditional_pair) {
            const int condition_line = block.end_line - 1;
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(graph, block.end_line + 1),
                    condition_line);
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(
                        graph,
                        ComputeJumpTargetLine(block.end_line,
                                              tail.sbx,
                                              instruction_count)),
                    condition_line);
            continue;
        }

        if (tail.opcode == Lua51Opcode::kJmp) {
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(graph,
                                      ComputeJumpTargetLine(block.end_line,
                                                            tail.sbx,
                                                            instruction_count)),
                    block.end_line);
            continue;
        }

        if (IsConditionalBranchOpcode(tail.opcode) &&
            block.end_line < instruction_count &&
            instructions[static_cast<std::size_t>(block.end_line)].opcode == Lua51Opcode::kJmp) {
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(graph, block.end_line + 2),
                    block.end_line);
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(
                        graph,
                        ComputeJumpTargetLine(block.end_line + 1,
                                              instructions[static_cast<std::size_t>(block.end_line)].sbx,
                                              instruction_count)),
                    block.end_line);
            continue;
        }

        if (tail.opcode == Lua51Opcode::kForPrep || tail.opcode == Lua51Opcode::kForLoop) {
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(graph, block.end_line + 1),
                    block.end_line);
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(graph,
                                      ComputeJumpTargetLine(block.end_line,
                                                            tail.sbx,
                                                            instruction_count)),
                    block.end_line);
            continue;
        }

        if (tail.opcode == Lua51Opcode::kTForLoop) {
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(graph, block.end_line + 1),
                    block.end_line);
            AddEdge(&graph,
                    block_index,
                    BlockIndexForLine(graph, block.end_line + 2),
                    block.end_line);
            continue;
        }

        if (tail.opcode == Lua51Opcode::kReturn || tail.opcode == Lua51Opcode::kTailCall) {
            continue;
        }

        AddEdge(&graph,
                block_index,
                BlockIndexForLine(graph, block.end_line + 1),
                block.end_line);
    }

    return graph;
}

bool HasIncomingEdgeIntoRange(const Lua51ControlFlowGraph& graph,
                              int target_start,
                              int target_end,
                              int ignore_source_line_begin,
                              int ignore_source_line_end) {
    if (target_start > target_end) {
        return false;
    }

    if (ignore_source_line_end < ignore_source_line_begin) {
        ignore_source_line_end = ignore_source_line_begin;
    }

    for (const Lua51ControlFlowEdge& edge : graph.edges) {
        if (edge.source_line >= ignore_source_line_begin &&
            edge.source_line <= ignore_source_line_end) {
            continue;
        }
        if (edge.source_block < 0 ||
            static_cast<std::size_t>(edge.source_block) >= graph.blocks.size()) {
            continue;
        }
        if (edge.target_block < 0 ||
            static_cast<std::size_t>(edge.target_block) >= graph.blocks.size()) {
            continue;
        }

        const Lua51BasicBlock& source_block =
            graph.blocks[static_cast<std::size_t>(edge.source_block)];
        const Lua51BasicBlock& target_block =
            graph.blocks[static_cast<std::size_t>(edge.target_block)];
        if (source_block.start_line >= target_start && source_block.start_line <= target_end) {
            continue;
        }
        if (target_block.start_line >= target_start && target_block.start_line <= target_end) {
            return true;
        }
    }

    return false;
}

bool CanReachTargetLine(const Lua51ControlFlowGraph& graph,
                        int start_line,
                        int target_line) {
    const int start_block = BlockIndexForLine(graph, start_line);
    const int target_block = BlockIndexForLine(graph, target_line);
    if (start_block < 0 || target_block < 0) {
        return false;
    }

    const std::unordered_set<int> blocked_blocks;
    return IsReachableAvoidingBlocks(graph, start_block, target_block, blocked_blocks);
}

bool IsStructuredSiblingBranchJump(const Lua51ControlFlowGraph& graph,
                                   int jump_line,
                                   int skipped_start_line,
                                   int jump_target_line) {
    if (jump_line < 1 || skipped_start_line != jump_line + 1 ||
        jump_target_line <= skipped_start_line) {
        return false;
    }

    const int source_block_index = BlockIndexForLine(graph, jump_line);
    const int skipped_block_index = BlockIndexForLine(graph, skipped_start_line);
    const int target_block_index = BlockIndexForLine(graph, jump_target_line);
    if (source_block_index < 0 || skipped_block_index < 0 || target_block_index < 0 ||
        source_block_index == skipped_block_index) {
        return false;
    }

    const Lua51BasicBlock& source_block =
        graph.blocks[static_cast<std::size_t>(source_block_index)];
    const Lua51BasicBlock& skipped_block =
        graph.blocks[static_cast<std::size_t>(skipped_block_index)];
    if (source_block.end_line != jump_line || skipped_block.start_line != skipped_start_line ||
        !source_block.ends_in_unconditional_jump) {
        return false;
    }

    const auto has_successor = [](const Lua51BasicBlock& block, int target_block) {
        return std::find(block.successors.begin(), block.successors.end(), target_block) !=
               block.successors.end();
    };
    if (!has_successor(source_block, target_block_index)) {
        return false;
    }

    std::unordered_set<int> blocked_blocks;
    blocked_blocks.insert(skipped_block_index);
    blocked_blocks.insert(target_block_index);

    for (const Lua51BasicBlock& predecessor : graph.blocks) {
        if (!predecessor.ends_in_conditional) {
            continue;
        }
        if (!has_successor(predecessor, skipped_block_index)) {
            continue;
        }

        for (int successor_index : predecessor.successors) {
            if (successor_index == skipped_block_index) {
                continue;
            }
            if (IsReachableAvoidingBlocks(
                    graph, successor_index, source_block_index, blocked_blocks)) {
                return true;
            }
        }
    }

    return false;
}

std::vector<int> CollectSharedJoinCandidateLines(const Lua51ControlFlowGraph& graph,
                                                 int then_start_line,
                                                 int else_start_line,
                                                 int min_join_line,
                                                 int max_join_line) {
    std::vector<int> candidate_lines;
    if (min_join_line > max_join_line) {
        return candidate_lines;
    }

    const int then_block = BlockIndexForLine(graph, then_start_line);
    const int else_block = BlockIndexForLine(graph, else_start_line);
    if (then_block < 0 || else_block < 0) {
        return candidate_lines;
    }

    const std::vector<bool> then_reachable =
        CollectReachableBlocks(graph, then_block, max_join_line);
    const std::vector<bool> else_reachable =
        CollectReachableBlocks(graph, else_block, max_join_line);

    for (const Lua51BasicBlock& block : graph.blocks) {
        if (block.index < 0 ||
            static_cast<std::size_t>(block.index) >= then_reachable.size() ||
            static_cast<std::size_t>(block.index) >= else_reachable.size() ||
            !then_reachable[static_cast<std::size_t>(block.index)] ||
            !else_reachable[static_cast<std::size_t>(block.index)]) {
            continue;
        }

        const int start_line = std::max(block.start_line, min_join_line);
        const int end_line = std::min(block.end_line, max_join_line);
        for (int line = start_line; line <= end_line; ++line) {
            candidate_lines.push_back(line);
        }
    }

    return candidate_lines;
}

int FindSharedJoinLine(const Lua51ControlFlowGraph& graph,
                       int then_start_line,
                       int else_start_line,
                       int min_join_line,
                       int max_join_line) {
    if (min_join_line > max_join_line) {
        return -1;
    }

    const std::vector<int> candidates =
        CollectSharedJoinCandidateLines(graph,
                                        then_start_line,
                                        else_start_line,
                                        min_join_line,
                                        max_join_line);
    if (candidates.empty()) {
        return -1;
    }

    int join_line = -1;
    for (int line : candidates) {
        if (line < min_join_line || line > max_join_line) {
            continue;
        }
        if (join_line == -1 || line < join_line) {
            join_line = line;
        }
    }
    return join_line;
}

}  // namespace refii::lua::decompiler
