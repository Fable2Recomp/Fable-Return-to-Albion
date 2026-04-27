#include "tools/lua_decompiler_internal.h"

extern "C" {
#include "lauxlib.h"
#include "lua.h"
}

#include <filesystem>
#include <iomanip>
#include <sstream>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace {

constexpr std::size_t kMaxTransientSourceCacheEntries = 192;

struct CompiledSourceCacheEntry {
    bool attempted = false;
    bool success = false;
    refii::lua::audit::CompiledLuaChunkInfo compiled;
};

struct ValidationCacheEntry {
    bool attempted = false;
    std::string error;
};

struct LuaDecompilerSourceSessionCache {
    std::unordered_map<std::string, CompiledSourceCacheEntry> compiled_sources;
    std::unordered_map<std::string, ValidationCacheEntry> validation_results;
    std::unordered_map<std::string, refii::lua::internal::ParsedChunkCacheEntry> parsed_chunks;
};

thread_local LuaDecompilerSourceSessionCache g_lua_decompiler_source_session_cache;

template <typename Map>
void ClearAndReleaseMap(Map* map) {
    if (map == nullptr || map->empty()) {
        return;
    }
    Map empty;
    map->swap(empty);
}

std::string BuildSourceCacheKey(std::string_view chunk_name, std::string_view source) {
    std::string key(chunk_name);
    key.push_back('\n');
    key.append(refii::lua::internal::ComputeStableBytecodeIdentity(source));
    return key;
}

void MaybeTrimTransientSourceCaches() {
    if (g_lua_decompiler_source_session_cache.compiled_sources.size() <=
            kMaxTransientSourceCacheEntries &&
        g_lua_decompiler_source_session_cache.validation_results.size() <=
            kMaxTransientSourceCacheEntries) {
        return;
    }

    ClearAndReleaseMap(&g_lua_decompiler_source_session_cache.compiled_sources);
    ClearAndReleaseMap(&g_lua_decompiler_source_session_cache.validation_results);
}

}  // namespace

namespace refii::lua::internal {

std::string ComputeStableBytecodeIdentity(std::string_view bytes) {
    std::uint64_t hash = 1469598103934665603ull;
    for (const char byte : bytes) {
        hash ^= static_cast<unsigned char>(byte);
        hash *= 1099511628211ull;
    }

    std::ostringstream stream;
    stream << bytes.size() << ":";
    stream << std::hex << std::setw(16) << std::setfill('0') << hash;
    return stream.str();
}

ParsedChunkCacheEntry& GetParsedChunkCacheEntry(const std::vector<char>& input_bytes) {
    const std::string cache_key = ComputeStableBytecodeIdentity(
        std::string_view(input_bytes.data(), input_bytes.size()));
    ParsedChunkCacheEntry& cache_entry =
        g_lua_decompiler_source_session_cache.parsed_chunks[cache_key];
    if (!cache_entry.attempted) {
        cache_entry.attempted = true;
        cache_entry.success =
            refii::lua::ParseLua51Chunk(input_bytes, &cache_entry.chunk, &cache_entry.parse_error);
        if (cache_entry.success) {
            cache_entry.chunk_stats = refii::lua::SummarizeLua51Chunk(cache_entry.chunk);
            cache_entry.operation_summary =
                refii::lua::SummarizeLua51Operations(cache_entry.chunk.main);
            cache_entry.control_flow_summary =
                refii::lua::SummarizeLua51ControlFlow(cache_entry.chunk.main);
        }
    }
    return cache_entry;
}

bool CompileLuaSourceToChunkCached(std::string_view source,
                                   std::string_view chunk_name,
                                   refii::lua::audit::CompiledLuaChunkInfo* output) {
    if (output == nullptr) {
        return false;
    }

    MaybeTrimTransientSourceCaches();
    const std::string cache_key = BuildSourceCacheKey(chunk_name, source);
    CompiledSourceCacheEntry& cache_entry =
        g_lua_decompiler_source_session_cache.compiled_sources[cache_key];
    const auto compile_into_cache = [&]() {
        cache_entry.attempted = true;
        cache_entry.compiled = {};
        cache_entry.success =
            refii::lua::audit::CompileLuaSourceToChunk(source, chunk_name, &cache_entry.compiled);
        if (!cache_entry.success && cache_entry.compiled.error.empty()) {
            cache_entry.compiled.error = ValidateLuaSource(source, chunk_name);
            if (cache_entry.compiled.error.empty()) {
                cache_entry.compiled.error = "compile_chunk_cache_failed";
            }
        }
    };
    if (!cache_entry.attempted) {
        compile_into_cache();
    } else if (!cache_entry.success && cache_entry.compiled.error.empty()) {
        // Recover from stale failed entries that carried no compiler diagnostics.
        compile_into_cache();
    }

    if (!cache_entry.success) {
        *output = cache_entry.compiled;
        return false;
    }

    *output = cache_entry.compiled;
    return true;
}

void ResetTransientSourceDecompileCaches() {
    ClearAndReleaseMap(&g_lua_decompiler_source_session_cache.compiled_sources);
    ClearAndReleaseMap(&g_lua_decompiler_source_session_cache.validation_results);
    ClearAndReleaseMap(&g_lua_decompiler_source_session_cache.parsed_chunks);
}

std::string ValidateLuaSource(std::string_view source, std::string_view chunk_name) {
    MaybeTrimTransientSourceCaches();
    const std::string cache_key = BuildSourceCacheKey(chunk_name, source);
    ValidationCacheEntry& cache_entry =
        g_lua_decompiler_source_session_cache.validation_results[cache_key];
    if (cache_entry.attempted) {
        return cache_entry.error;
    }

    cache_entry.attempted = true;
    lua_State* state = luaL_newstate();
    if (state == nullptr) {
        cache_entry.error = "validator_init_failed";
        return cache_entry.error;
    }

    const std::string name(chunk_name.empty() ? "decompiled_chunk" : chunk_name);
    const int status = luaL_loadbuffer(state, source.data(), source.size(), name.c_str());
    if (status != 0) {
        if (const char* message = lua_tostring(state, -1); message != nullptr) {
            cache_entry.error = message;
        } else {
            cache_entry.error = "syntax_invalid";
        }
    }

    lua_close(state);
    return cache_entry.error;
}

std::string ValidateLuaRepairSource(std::string_view source) {
    return ValidateLuaSource(source, "decompiled_chunk");
}

}  // namespace refii::lua::internal
