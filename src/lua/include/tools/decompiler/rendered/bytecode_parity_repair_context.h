#pragma once

#include "tools/bytecode/lua_bytecode_oracles.h"
#include "tools/native/native_lua_chunk.h"

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

struct BytecodeParityRepairContext {
    refii::lua::Lua51Chunk original_chunk;
    refii::lua::audit::CompiledLuaChunkInfo compiled_candidate;
    refii::lua::audit::FunctionMismatchInfo mismatch;
};

const refii::lua::Lua51Function* GetLuaFunctionByPreorderIndex(
    const refii::lua::Lua51Chunk& chunk,
    std::size_t function_index);

bool Lua51FunctionBytecodeExact(const refii::lua::Lua51Function& original,
                                const refii::lua::Lua51Function& candidate);
bool Lua51FunctionBytecodeExactInChunk(const refii::lua::Lua51Chunk& original_chunk,
                                       const refii::lua::Lua51Chunk& candidate_chunk,
                                       std::size_t function_index);

bool IsFirstFunctionMismatchNotWorse(
    const refii::lua::audit::FunctionMismatchInfo& candidate,
    const refii::lua::audit::FunctionMismatchInfo& baseline);

bool BuildBytecodeParityRepairContext(const std::vector<char>& input_bytes,
                                      std::string_view candidate_source,
                                      BytecodeParityRepairContext* context_out);

long long BytecodeParityMismatchDeltaSum(
    const refii::lua::audit::FunctionMismatchInfo& mismatch);
bool IsBytecodeParityStrictMismatchImprovement(
    const BytecodeParityRepairContext& context,
    const refii::lua::audit::FunctionMismatchInfo& transformed_mismatch);

bool TryCommitBytecodeParityImprovingSource(
    const BytecodeParityRepairContext& context,
    std::string transformed_source,
    std::string_view compile_label,
    std::string* candidate_source);
bool TryCommitBytecodeParityImprovingLines(
    const BytecodeParityRepairContext& context,
    const std::vector<std::string>& transformed_lines,
    bool had_trailing_newline,
    std::string_view compile_label,
    std::string* candidate_source);
bool TryCommitBytecodeParityExactSource(
    const BytecodeParityRepairContext& context,
    std::string transformed_source,
    std::string_view compile_label,
    std::string* candidate_source);
bool TryCommitBytecodeParityExactLines(
    const BytecodeParityRepairContext& context,
    const std::vector<std::string>& transformed_lines,
    bool had_trailing_newline,
    std::string_view compile_label,
    std::string* candidate_source);
bool TryCommitBytecodeParityExactCurrentFunctionLines(
    const BytecodeParityRepairContext& context,
    const std::vector<std::string>& transformed_lines,
    bool had_trailing_newline,
    std::string_view compile_label,
    std::string* candidate_source);

}  // namespace refii::lua::internal
