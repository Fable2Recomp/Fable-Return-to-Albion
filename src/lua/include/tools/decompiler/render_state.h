#pragma once

#include "tools/decompiler/straight_line_render.h"

#include <cstddef>
#include <string>
#include <vector>

namespace refii::lua {

struct LuaRenderStateContext {
    const std::vector<Lua51Instruction>& instructions;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
};

struct LuaBranchStateSnapshot {
    std::vector<StraightLineValue> values;
    std::vector<bool> declared;
};

struct LuaMergedBranchState {
    std::vector<StraightLineValue> values;
    std::vector<bool> declared;
    std::vector<bool> written_registers;
};

void EmitLuaNewLocalDeclarations(std::vector<std::string>* lines,
                                 std::vector<StraightLineValue>* values,
                                 std::vector<bool>* declared,
                                 const LuaRenderStateContext& context,
                                 int pc);

void InvalidateLuaRenderValues(std::vector<StraightLineValue>* values,
                               const std::vector<bool>& declared,
                               const LuaRenderStateContext& context,
                               int line);

std::vector<bool> CollectWrittenLuaRegistersInRange(const std::vector<Lua51Instruction>& instructions,
                                                    int register_count,
                                                    int start_line,
                                                    int end_line);

void ClearLuaWrittenJoinState(std::vector<StraightLineValue>* values,
                              std::vector<bool>* declared,
                              const std::vector<bool>& written_registers,
                              int register_count);

bool TryMergeLuaPreviewRegisterFallback(const std::vector<StraightLineValue>& original_values,
                                        const std::vector<bool>* original_declared,
                                        const std::vector<StraightLineValue>& fallback_values,
                                        const std::vector<bool>* fallback_declared,
                                        const RegisterDeclarations& declarations,
                                        const Lua51Function& function,
                                        int line,
                                        int register_count,
                                        int preferred_reg,
                                        bool require_temporary_declaration,
                                        StraightLineValue* merged_value);

void MergeLuaJoinDeclaredState(std::vector<bool>* declared,
                               const std::vector<bool>& first_branch_declared,
                               const std::vector<bool>& second_branch_declared,
                               const std::vector<bool>& candidate_registers,
                               int register_count);

void MergeLuaJoinSafeValues(std::vector<StraightLineValue>* values,
                            std::vector<bool>* declared,
                            const std::vector<StraightLineValue>& first_branch_values,
                            const std::vector<bool>& first_branch_declared,
                            const std::vector<StraightLineValue>& second_branch_values,
                            const std::vector<bool>& second_branch_declared,
                            const RegisterDeclarations& declarations,
                            const Lua51Function& function,
                            int line,
                            const std::vector<bool>& candidate_registers,
                            int register_count);

void CanonicalizeLuaJoinCandidateValues(std::vector<StraightLineValue>* values,
                                        const std::vector<bool>& candidate_registers,
                                        const RegisterDeclarations& declarations,
                                        const Lua51Function& function,
                                        int line,
                                        int register_count);

void FinalizeLuaJoinCandidateState(std::vector<StraightLineValue>* values,
                                   std::vector<bool>* declared,
                                   const std::vector<StraightLineValue>& saved_values,
                                   const std::vector<bool>& saved_declared,
                                   const std::vector<bool>& written_registers,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int line,
                                   int register_count);

void RestoreLuaJoinRegisterIdentities(std::vector<StraightLineValue>* values,
                                      const std::vector<bool>& declared,
                                      const std::vector<bool>& written_registers,
                                      const RegisterDeclarations& declarations,
                                      const Lua51Function& function,
                                      int line,
                                      int register_count);

LuaMergedBranchState BuildLuaMergedBranchState(
    const LuaBranchStateSnapshot& base_state,
    const LuaBranchStateSnapshot& first_branch_state,
    const std::vector<bool>& first_written_registers,
    const LuaBranchStateSnapshot& second_branch_state,
    const std::vector<bool>& second_written_registers,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    int line,
    int register_count);

void RestoreUnmodifiedSafeLuaValues(std::vector<StraightLineValue>* values,
                                    std::vector<bool>* declared,
                                    const std::vector<StraightLineValue>& saved_values,
                                    const std::vector<bool>& saved_declared,
                                    const std::vector<bool>& written_registers,
                                    const RegisterDeclarations& declarations,
                                    const Lua51Function& function,
                                    int line,
                                    int register_count);

bool LastRenderedStatementIsTerminal(const std::vector<std::string>& lines,
                                     std::size_t start_line_count);

}  // namespace refii::lua
