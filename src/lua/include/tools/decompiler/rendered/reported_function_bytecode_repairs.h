#pragma once

#include "tools/native/native_lua_chunk.h"

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

using LuaFunctionSourceBytecodeExactFn =
    bool (*)(const refii::lua::Lua51Chunk& original_chunk,
             std::size_t function_index,
             const std::vector<std::string>& function_lines);
using LuaFunctionSourceImprovedInIsolationFn =
    bool (*)(const refii::lua::Lua51Chunk& original_chunk,
             std::size_t function_index,
             const std::vector<std::string>& before_lines,
             const std::vector<std::string>& after_lines);

struct ReportedFunctionBytecodeRepairOptions {
    LuaFunctionSourceBytecodeExactFn function_source_bytecode_exact = nullptr;
    LuaFunctionSourceImprovedInIsolationFn function_source_improved_in_isolation = nullptr;
};

bool TryRepairReportedFunctionFromBytecode(
    const std::vector<char>& input_bytes,
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note,
    std::string_view note_prefix,
    const ReportedFunctionBytecodeRepairOptions& options);

bool TryRepairReportedFunctionsFromBytecodeIteratively(
    const std::vector<char>& input_bytes,
    std::string& decompiled_source,
    std::string* validation_error,
    std::string* note,
    std::string_view note_prefix,
    const ReportedFunctionBytecodeRepairOptions& options);

}  // namespace refii::lua::internal
