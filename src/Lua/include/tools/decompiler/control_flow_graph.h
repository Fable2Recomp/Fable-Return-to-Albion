#pragma once

#include "tools/decompiler/lua51_analysis.h"

#include <vector>

namespace refii::lua::decompiler {

struct Lua51ControlFlowEdge {
    int source_block = -1;
    int target_block = -1;
    int source_line = 0;
};

struct Lua51BasicBlock {
    int index = -1;
    int start_line = 0;
    int end_line = 0;
    bool ends_in_conditional = false;
    bool ends_in_unconditional_jump = false;
    std::vector<int> predecessors;
    std::vector<int> successors;
};

struct Lua51ControlFlowGraph {
    std::vector<Lua51BasicBlock> blocks;
    std::vector<int> line_to_block;
    std::vector<Lua51ControlFlowEdge> edges;
};

bool IsConditionalBranchOpcode(Lua51Opcode opcode);

int ComputeJumpTargetLine(int pc, int sbx, int instruction_count);

int ResolveTerminalJumpTarget(const std::vector<Lua51Instruction>& instructions,
                              int target_line,
                              int max_depth = 8);

Lua51ControlFlowGraph BuildLua51ControlFlowGraph(
    const std::vector<Lua51Instruction>& instructions);

bool HasIncomingEdgeIntoRange(const Lua51ControlFlowGraph& graph,
                              int target_start,
                              int target_end,
                              int ignore_source_line);

bool CanReachTargetLine(const Lua51ControlFlowGraph& graph,
                        int start_line,
                        int target_line);

bool IsStructuredSiblingBranchJump(const Lua51ControlFlowGraph& graph,
                                   int jump_line,
                                   int skipped_start_line,
                                   int jump_target_line);

std::vector<int> CollectSharedJoinCandidateLines(const Lua51ControlFlowGraph& graph,
                                                 int then_start_line,
                                                 int else_start_line,
                                                 int min_join_line,
                                                 int max_join_line);

}  // namespace refii::lua::decompiler
