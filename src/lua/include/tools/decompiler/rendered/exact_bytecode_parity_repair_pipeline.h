#pragma once

#include "tools/native/native_lua_decompiler.h"

#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

struct ExactBytecodeParityRepairPipelineCallbacks {
    bool (*apply_instruction_cleanup_bundle)(std::vector<std::string>* lines,
                                             bool include_local_timing,
                                             bool include_tiny_microdiff,
                                             bool include_polarity_cleanup) = nullptr;
    bool (*apply_instruction_polarity_cleanup)(std::vector<std::string>* lines) =
        nullptr;
    bool (*normalize_create_subclass_seeds)(std::vector<std::string>* lines) =
        nullptr;
    bool (*normalize_singleton_index_table_assignments)(
        std::vector<std::string>* lines) = nullptr;
    bool (*try_render_native_main_chunk_candidate)(
        const std::vector<char>& input_bytes,
        std::string* decompiled_source,
        std::string* note,
        bool simplify_rendered_output,
        std::string_view note_prefix,
        std::string_view source_name_hint) = nullptr;
};

bool TryRepairNativeCandidateBytecodeParityWithCallbacks(
    const std::vector<char>& input_bytes,
    std::string& candidate_source,
    std::string* note,
    std::string_view note_prefix,
    const LuaWorkspaceDecompileOptions* options,
    const std::filesystem::path* input_path,
    const ExactBytecodeParityRepairPipelineCallbacks& callbacks);

}  // namespace refii::lua::internal
