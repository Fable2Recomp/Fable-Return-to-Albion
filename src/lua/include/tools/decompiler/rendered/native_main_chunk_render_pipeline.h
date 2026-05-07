#pragma once

#include "tools/native/native_lua_decompiler.h"

#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

struct NativeMainChunkRenderCacheEntry {
    bool attempted = false;
    bool success = false;
    std::string rendered_source;
    std::vector<std::string> note_suffixes;
};

struct NativeMainChunkRenderPipelineCallbacks {
    NativeMainChunkRenderCacheEntry& (*main_chunk_cache_entry)(
        const std::string& cache_key) = nullptr;
    void (*trim_main_chunk_cache)() = nullptr;
    bool (*is_known_quest_hotspot_path)(std::string_view normalized_path) = nullptr;
    bool (*has_validated_exact_repair_no_progress_signal)(std::string_view note) =
        nullptr;
    bool (*try_repair_native_candidate_bytecode_parity)(
        const std::vector<char>& input_bytes,
        std::string& decompiled_source,
        std::string* note,
        std::string_view note_prefix,
        const LuaWorkspaceDecompileOptions* options,
        const std::filesystem::path* input_path) = nullptr;
};

bool TryRenderNativeMainChunkCandidateViaTool(
    const LuaWorkspaceDecompileOptions& options,
    const std::filesystem::path& input_path,
    const std::vector<char>& input_bytes,
    bool simplify_rendered_output,
    std::string* decompiled_source,
    std::string* note,
    std::string_view note_prefix,
    const NativeMainChunkRenderPipelineCallbacks& callbacks);

bool TryRenderNativeMainChunkCandidate(
    const std::vector<char>& input_bytes,
    std::string* decompiled_source,
    std::string* note,
    bool simplify_rendered_output,
    std::string_view note_prefix,
    std::string_view source_name_hint,
    const NativeMainChunkRenderPipelineCallbacks& callbacks);

bool TryReplaceWithValidatedNativeMainChunk(
    const std::vector<char>& input_bytes,
    std::string& decompiled_source,
    std::string* note,
    const LuaWorkspaceDecompileOptions* options,
    const std::filesystem::path* input_path,
    std::string_view preferred_render_mode,
    const NativeMainChunkRenderPipelineCallbacks& callbacks);

}  // namespace refii::lua::internal
