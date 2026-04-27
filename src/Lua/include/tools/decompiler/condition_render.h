#pragma once

#include "tools/decompiler/control_flow_graph.h"
#include "tools/decompiler/straight_line_render.h"

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

using LuaInlineRangeEvaluator =
    std::function<bool(int, int, std::vector<StraightLineValue>*, std::string*)>;

struct LuaConditionalChainLink {
    int condition_line = 0;
    int jump_line = 0;
    int jump_target_line = 0;
    int resolved_jump_target_line = 0;
    int effective_target_line = 0;
    Lua51Opcode opcode = Lua51Opcode::kMove;
    int target_register = -1;
    int source_register = -1;
};

struct LuaConditionalChainSpan {
    int chain_start_line = 0;
    int next_line = 0;
    int join_line = 0;
    std::vector<LuaConditionalChainLink> links;
};

struct LuaConditionalStatementTempOwnership {
    bool proven = false;
    bool unambiguous = false;
    bool call_value_owned = false;
    int effective_join_line = -1;
    int call_value_register = -1;
    int call_value_producer_line = -1;
    std::uint64_t call_value_fingerprint = 0;
};

enum class LuaJoinOwnershipCommitKind {
    kUnproven = 0,
    kCommitInTrueBranch,
    kCommitInFalseBranch,
    kCommitAtJoin,
};

struct LuaJoinOwnershipCommitDecision {
    bool proven = false;
    bool unambiguous = false;
    bool call_value_owned = false;
    int effective_join_line = -1;
    int target_register = -1;
    std::uint64_t chain_fingerprint = 0;
    LuaJoinOwnershipCommitKind commit_kind = LuaJoinOwnershipCommitKind::kUnproven;
};

struct LuaConditionalChainCommitResolution {
    LuaConditionalChainSpan chain_span;
    LuaJoinOwnershipCommitDecision ownership_commit;
    int branch_target_line = -1;
    bool proven = false;
};

struct LuaConditionalRootCommitPacket {
    LuaConditionalChainSpan chain_span;
    LuaJoinOwnershipCommitDecision ownership_commit;
    int branch_target_line = -1;
    int effective_join_line = -1;
    int carried_register = -1;
    LuaJoinOwnershipCommitKind commit_kind = LuaJoinOwnershipCommitKind::kUnproven;
    bool chain_proven = false;
    bool ownership_proven = false;
    bool call_value_owned = false;
};

bool IsLuaScopedLargeHoldoutOwnershipPath(std::string_view source_name);
bool IsLuaScopedSmallHoldoutOwnershipPath(std::string_view source_name);
bool IsLuaAiCoreHotspotOwnershipPath(std::string_view source_name);
bool IsLuaPriorityOwnershipPath(std::string_view source_name);
bool IsLuaInteractiveCutsceneActionsOwnershipPath(std::string_view source_name);
bool IsLuaScopedLargeHoldoutOwnershipFunction(const Lua51Function& function);
bool IsLuaScopedSmallHoldoutOwnershipFunction(const Lua51Function& function);
bool IsLuaAiCoreHotspotOwnershipFunction(const Lua51Function& function);
bool IsLuaPriorityOwnershipFunction(const Lua51Function& function);
bool IsLuaInteractiveCutsceneActionsOwnershipFunction(const Lua51Function& function);
bool IsLuaInteractiveCutsceneActionsF2OwnershipFunction(const Lua51Function& function);

int ResolveLuaConditionalOwnershipRegisterForInstruction(const Lua51Instruction& instruction,
                                                         int register_count);

int ResolveLuaConditionalOwnershipRegisterForChainLink(const LuaConditionalChainLink& link,
                                                       int register_count);

std::string RenderLuaConditionWithValues(const std::vector<StraightLineValue>& state_values,
                                         const RegisterDeclarations& declarations,
                                         const Lua51Function& function,
                                         const Lua51Instruction& instruction,
                                         int line);

std::string RenderLuaConditionWithFunctionBoundaryParity(
    const std::vector<StraightLineValue>& state_values,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    const Lua51Instruction& instruction,
    int line);

bool TryBuildLuaConditionalChainLink(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int chain_start_line,
    int condition_line,
    int max_target_line,
    int expected_target_line,
    LuaConditionalChainLink* link_out,
    const Lua51Function* function_hint = nullptr);

bool TryCollectLuaConditionalChainSpan(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int chain_start_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    LuaConditionalChainSpan* span_out,
    const Lua51Function* function_hint = nullptr);

bool TryClassifyLuaConditionalStatementTempOwnership(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int condition_line,
    int branch_target_line,
    int join_line,
    int target_register,
    int consumed_span_start,
    int consumed_span_end,
    LuaConditionalStatementTempOwnership* ownership_out);

bool TryBuildLuaJoinOwnershipCommitDecision(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int condition_line,
    int branch_target_line,
    int join_line,
    int target_register,
    LuaJoinOwnershipCommitDecision* decision_out,
    const Lua51Function* function_hint = nullptr);

bool TryResolveLuaConditionalChainCommit(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    int register_count,
    LuaConditionalChainCommitResolution* resolution_out,
    const Lua51Function* function_hint = nullptr);

bool TryResolveLuaConditionalRootCommitPacket(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    int register_count,
    int fallback_branch_target_line,
    int fallback_join_line,
    LuaConditionalRootCommitPacket* packet_out,
    const Lua51Function* function_hint = nullptr);

bool IsLuaConditionalRootPacketReusableForBranch(
    const LuaConditionalRootCommitPacket& packet,
    int condition_line,
    int branch_target_line);

void ApplyLuaConditionalChainTestSetCarryEffects(
    const std::vector<Lua51Instruction>& instructions,
    const LuaConditionalChainSpan& chain_span,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    std::vector<StraightLineValue>* preview_values,
    int carried_register_filter = -1,
    bool apply_once = false);

void AppendLuaChainedAndConditions(std::string* combined_condition,
                                   int* combined_then_start,
                                   int branch_target,
                                   const decompiler::Lua51ControlFlowGraph* control_flow_graph,
                                   const std::vector<Lua51Instruction>& instructions,
                                   std::vector<StraightLineValue>* preview_values,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   const LuaInlineRangeEvaluator& evaluate_inline_range);

}  // namespace refii::lua
