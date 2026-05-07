#pragma once

#include "tools/decompiler/rendered/bytecode_parity_repair_context.h"

#include <string>
#include <vector>

namespace refii::lua::internal {

using RenderedLuaLineRepairFn = bool (*)(std::vector<std::string>* lines);

struct TinyMainRepairOptions {
    RenderedLuaLineRepairFn normalize_create_subclass_seeds = nullptr;
    RenderedLuaLineRepairFn normalize_singleton_index_table_assignments = nullptr;
};

bool TryRepairTinyMainCreateSubClassSeedOwnership(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source,
    const TinyMainRepairOptions& options);
bool TryRepairTinyMainSingletonIndexTableAssignments(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source,
    const TinyMainRepairOptions& options);
bool TryRepairTinyMainRepeatedGlobalFieldWriteAlias(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source);
bool TryRepairTinyMainTopLevelFieldSeedCluster(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source);
bool TryRepairWhileConditionInitializerCallCarry(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source);
bool TryRepairLocalDiffParameterNameMismatch(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source);

}  // namespace refii::lua::internal
