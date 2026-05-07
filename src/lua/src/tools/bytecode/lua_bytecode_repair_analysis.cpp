#include "tools/bytecode/lua_bytecode_repair_analysis.h"

#include "tools/decompiler/rendered/bytecode_parity_repair_context.h"
#include "tools/bytecode/lua_bytecode_oracles.h"

#include <array>
#include <cmath>
#include <cstdlib>

namespace refii::lua::internal {
namespace {

std::array<long long, 3> DescribeLuaFunctionDeltaTuple(
    const refii::lua::audit::Lua51FunctionStats& original,
    const refii::lua::audit::Lua51FunctionStats& candidate) {
    return {
        std::llabs(static_cast<long long>(candidate.instruction_count) -
                   static_cast<long long>(original.instruction_count)),
        std::llabs(static_cast<long long>(candidate.constant_count) -
                   static_cast<long long>(original.constant_count)),
        std::llabs(static_cast<long long>(candidate.local_count) -
                   static_cast<long long>(original.local_count)),
    };
}

}  // namespace

bool IsLuaFunctionStatsImproved(const refii::lua::Lua51Chunk& original_chunk,
                                const refii::lua::Lua51Chunk& before_chunk,
                                const refii::lua::Lua51Chunk& after_chunk,
                                std::size_t function_index) {
    const refii::lua::Lua51Function* original =
        GetLuaFunctionByPreorderIndex(original_chunk, function_index);
    const refii::lua::Lua51Function* before =
        GetLuaFunctionByPreorderIndex(before_chunk, function_index);
    const refii::lua::Lua51Function* after =
        GetLuaFunctionByPreorderIndex(after_chunk, function_index);
    if (original == nullptr || before == nullptr || after == nullptr) {
        return false;
    }

    const auto original_stats = refii::lua::audit::SummarizeLua51FunctionStats(*original);
    const auto before_stats = refii::lua::audit::SummarizeLua51FunctionStats(*before);
    const auto after_stats = refii::lua::audit::SummarizeLua51FunctionStats(*after);

    const bool before_exact = Lua51FunctionBytecodeExact(*original, *before);
    const bool after_exact = Lua51FunctionBytecodeExact(*original, *after);
    if (after_exact && !before_exact) {
        return true;
    }
    if (before_exact) {
        return false;
    }

    const bool before_stats_exact =
        refii::lua::audit::Lua51FunctionStatsEqual(original_stats, before_stats);
    const bool after_stats_exact =
        refii::lua::audit::Lua51FunctionStatsEqual(original_stats, after_stats);
    if (after_stats_exact && !before_stats_exact) {
        return true;
    }
    if (before_stats_exact && !after_stats_exact) {
        return false;
    }

    return DescribeLuaFunctionDeltaTuple(original_stats, after_stats) <
           DescribeLuaFunctionDeltaTuple(original_stats, before_stats);
}

std::vector<std::size_t> CollectLuaMismatchedFunctionIndices(
    const refii::lua::Lua51Chunk& original_chunk,
    const refii::lua::Lua51Chunk& candidate_chunk) {
    std::vector<std::size_t> mismatches;
    for (std::size_t function_index = 1;; ++function_index) {
        const refii::lua::Lua51Function* original =
            GetLuaFunctionByPreorderIndex(original_chunk, function_index);
        const refii::lua::Lua51Function* candidate =
            GetLuaFunctionByPreorderIndex(candidate_chunk, function_index);
        if (original == nullptr || candidate == nullptr) {
            break;
        }

        if (!Lua51FunctionBytecodeExact(*original, *candidate)) {
            mismatches.push_back(function_index);
        }
    }
    return mismatches;
}

}  // namespace refii::lua::internal
