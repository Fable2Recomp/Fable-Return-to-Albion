#pragma once

#include "tools/decompiler/rendered/bytecode_parity_repair_context.h"

#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

using LuaNativeMainChunkRenderFn = bool (*)(const std::vector<char>& input_bytes,
                                           std::string* decompiled_source,
                                           std::string* note,
                                           bool simplify_rendered_output,
                                           std::string_view note_prefix,
                                           std::string_view source_name_hint);

using LuaInstructionCleanupBundleFn = bool (*)(std::vector<std::string>* lines,
                                               bool allow_direct_call_cleanup,
                                               bool allow_condition_cleanup,
                                               bool allow_table_cleanup);

bool TryRepairInstructionDiffFunctionFromReference(
    const BytecodeParityRepairContext& context,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix,
    LuaNativeMainChunkRenderFn render_native_main_chunk);

bool TryRepairTinyMainFunctionStatsFromReference(
    const BytecodeParityRepairContext& context,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix,
    LuaNativeMainChunkRenderFn render_native_main_chunk,
    LuaInstructionCleanupBundleFn apply_instruction_cleanup_bundle);

bool TryRepairTinyMainTopLevelFromReference(
    const BytecodeParityRepairContext& context,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix,
    LuaNativeMainChunkRenderFn render_native_main_chunk);

}  // namespace refii::lua::internal
