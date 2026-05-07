#pragma once

#include "tools/decompiler/rendered/bytecode_parity_repair_context.h"

#include <string>

namespace refii::lua::internal {

bool TryRepairMissingElseTrailingFunctionSuffix(const BytecodeParityRepairContext& context,
                                                std::string* candidate_source);

bool TryRepairTinyGroupMembersSingletonFieldAssignments(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source);

}  // namespace refii::lua::internal
