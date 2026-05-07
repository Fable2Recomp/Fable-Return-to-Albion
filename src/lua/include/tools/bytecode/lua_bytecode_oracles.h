#pragma once

#include "tools/native/native_lua_chunk.h"
#include "tools/native/native_lua_decompiler.h"

#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::audit {

struct Lua51FunctionStats {
    std::size_t instruction_count = 0;
    std::size_t local_count = 0;
    std::size_t constant_count = 0;
    std::size_t string_constant_count = 0;
    std::size_t child_function_count = 0;
    uint8_t upvalue_count = 0;
    uint8_t parameter_count = 0;
    uint8_t vararg_flags = 0;
    uint8_t max_stack_size = 0;
};

Lua51FunctionStats SummarizeLua51FunctionStats(const refii::lua::Lua51Function& function);
bool Lua51FunctionStatsEqual(const Lua51FunctionStats& lhs, const Lua51FunctionStats& rhs);
std::string DescribeLua51FunctionStats(const Lua51FunctionStats& stats);
std::string DescribeLua51ChunkStatsWithBytes(const refii::lua::Lua51ChunkStats& stats,
                                             std::uintmax_t byte_size);

struct FunctionMismatchInfo {
    bool available = false;
    std::size_t function_index = 0;
    uint32_t line_defined = 0;
    long long instruction_delta = 0;
    long long constant_delta = 0;
    long long local_delta = 0;
    std::string note;
    std::string diagnostic_note;
};

FunctionMismatchInfo FindFirstFunctionMismatch(const refii::lua::Lua51Chunk& original,
                                               const refii::lua::Lua51Chunk& candidate);
std::string DescribeMismatchForSummary(const FunctionMismatchInfo& mismatch);

struct CompiledLuaChunkInfo {
    bool source_available = false;
    bool syntax_valid = false;
    bool chunk_parsed = false;
    std::string error;
    std::vector<char> bytes;
    refii::lua::Lua51Chunk chunk;
    refii::lua::Lua51ChunkStats full_stats;
    Lua51FunctionStats main_stats;
    std::uintmax_t byte_size = 0;
};

struct LuaBytecodeParityReference {
    bool available = false;
    refii::lua::Lua51Chunk chunk;
    refii::lua::Lua51ChunkStats full_stats;
    Lua51FunctionStats main_stats;
    std::uintmax_t byte_size = 0;
};

struct LuaBytecodeParityResult {
    bool reference_available = false;
    bool candidate_compiled = false;
    bool candidate_chunk_parsed = false;
    bool main_stats_exact = false;
    bool full_stats_exact = false;
    bool exact_bytecode = false;
    std::string error;
    FunctionMismatchInfo first_mismatch;
    CompiledLuaChunkInfo compiled_candidate;
};

bool CompileLuaSourceToChunk(std::string_view source,
                             std::string_view chunk_name,
                             CompiledLuaChunkInfo* output);
bool BuildLuaBytecodeParityReference(const std::vector<char>& original_bytes,
                                     LuaBytecodeParityReference* output);
bool EvaluateCompiledLuaChunkBytecodeParity(const LuaBytecodeParityReference& reference,
                                            const CompiledLuaChunkInfo& compiled_candidate,
                                            LuaBytecodeParityResult* output);
bool EvaluateLuaSourceBytecodeParity(const LuaBytecodeParityReference& reference,
                                     std::string_view source,
                                     std::string_view chunk_name,
                                     LuaBytecodeParityResult* output);
bool TryRenderUnluacWholeFile(const refii::lua::LuaBytecodeParityAuditOptions& options,
                              const std::filesystem::path& input_path,
                              std::string* rendered_source,
                              std::string* note);

}  // namespace refii::lua::audit
