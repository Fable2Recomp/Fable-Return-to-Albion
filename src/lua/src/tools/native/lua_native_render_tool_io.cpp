#include "tools/native/lua_native_render_tool_io.h"

#include "tools/decompiler/decompiler_file_io.h"
#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/native/lua_native_main_chunk_policy.h"

#include <cctype>
#include <cstdlib>
#include <filesystem>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace refii::lua::internal {
namespace {

constexpr std::size_t kMaxTransientRenderedFunctionCacheEntries = 32;

struct RenderedFunctionCacheEntry {
    bool attempted = false;
    bool success = false;
    std::string rendered_function;
};

thread_local std::unordered_map<std::string, RenderedFunctionCacheEntry>
    g_rendered_function_cache;

std::string BuildRenderedFunctionCacheKey(const std::filesystem::path& input_path,
                                          std::size_t function_index,
                                          std::string_view mode) {
    std::string key =
        std::filesystem::absolute(input_path).lexically_normal().generic_string();
    for (char& ch : key) {
        if (ch == '\\') {
            ch = '/';
        } else {
            ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
        }
    }
    key.push_back('\n');
    key.append(mode);
    key.push_back('\n');
    key.append(std::to_string(function_index));
    return key;
}

void MaybeTrimTransientRenderedFunctionCache() {
    if (g_rendered_function_cache.size() <= kMaxTransientRenderedFunctionCacheEntries) {
        return;
    }

    std::unordered_map<std::string, RenderedFunctionCacheEntry> empty;
    g_rendered_function_cache.swap(empty);
}

bool TryRenderFunctionViaToolCached(const refii::lua::LuaWorkspaceDecompileOptions& options,
                                    const std::filesystem::path& input_path,
                                    std::size_t function_index,
                                    std::string_view cache_mode,
                                    const std::vector<std::string>& extra_args,
                                    bool force_external_tool,
                                    unsigned int timeout_seconds,
                                    std::string_view note_prefix,
                                    std::string* rendered_function,
                                    std::string* note) {
    if (rendered_function == nullptr || function_index == 0) {
        return false;
    }

    MaybeTrimTransientRenderedFunctionCache();
    const std::string cache_key =
        BuildRenderedFunctionCacheKey(input_path, function_index, cache_mode);
    RenderedFunctionCacheEntry& cache_entry = g_rendered_function_cache[cache_key];
    if (cache_entry.attempted) {
        if (!cache_entry.success) {
            return false;
        }
        *rendered_function = cache_entry.rendered_function;
        return true;
    }

    cache_entry.attempted = true;
    const std::filesystem::path temp_output = MakeNativeMainChunkTempOutputPath(input_path);
    std::error_code cleanup_error;
    const int exit_code = RunDecompiler(options,
                                        input_path,
                                        temp_output,
                                        extra_args,
                                        force_external_tool,
                                        timeout_seconds);
    if (exit_code != 0 || !std::filesystem::exists(temp_output) ||
        std::filesystem::file_size(temp_output) == 0) {
        std::filesystem::remove(temp_output, cleanup_error);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + DescribeRunResult("render", exit_code) +
                                  "_idx=" + std::to_string(function_index));
        }
        return false;
    }

    std::vector<char> rendered_bytes;
    const bool read_ok = ReadFile(temp_output, rendered_bytes);
    std::filesystem::remove(temp_output, cleanup_error);
    if (!read_ok) {
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "read_failed_idx=" +
                                  std::to_string(function_index));
        }
        return false;
    }

    cache_entry.success = true;
    cache_entry.rendered_function.assign(rendered_bytes.begin(), rendered_bytes.end());
    *rendered_function = cache_entry.rendered_function;
    return true;
}

} // namespace

void ResetNativeRenderToolIoCaches() {
    std::unordered_map<std::string, RenderedFunctionCacheEntry> empty;
    g_rendered_function_cache.swap(empty);
}

bool TryRenderNativeFunctionViaTool(const refii::lua::LuaWorkspaceDecompileOptions& options,
                                    const std::filesystem::path& input_path,
                                    std::size_t function_index,
                                    std::string* rendered_function,
                                    std::string* note) {
    return TryRenderFunctionViaToolCached(options,
                                          input_path,
                                          function_index,
                                          "native",
                                          {"--native-render", "-f", std::to_string(function_index)},
                                          true,
                                          options.native_render_timeout_seconds,
                                          "native_function_subprocess_",
                                          rendered_function,
                                          note);
}

void MaybeDumpInvalidNativeCandidate(std::string_view note_prefix,
                                     const std::filesystem::path* input_path,
                                     std::string_view source,
                                     std::string_view validation_error) {
    const char* dump_root_env = std::getenv("REFII_LUA_DUMP_INVALID_CANDIDATES");
    if (dump_root_env == nullptr || *dump_root_env == '\0' || source.empty() ||
        validation_error.empty()) {
        return;
    }

    std::filesystem::path relative_path;
    if (input_path != nullptr && !input_path->empty()) {
        relative_path = input_path->is_absolute() ? input_path->relative_path() : *input_path;
    } else if (!note_prefix.empty()) {
        std::string sanitized(note_prefix);
        for (char& ch : sanitized) {
            const unsigned char uch = static_cast<unsigned char>(ch);
            if (std::isalnum(uch) == 0 && ch != '_' && ch != '-' && ch != '.') {
                ch = '_';
            }
        }
        relative_path = sanitized + ".lua";
    } else {
        relative_path = "candidate.lua";
    }

    std::filesystem::path candidate_path = std::filesystem::path(dump_root_env) / relative_path;
    candidate_path += ".invalid.lua";
    std::filesystem::path error_path(candidate_path);
    error_path += ".error.txt";

    WriteTextFile(candidate_path, source);
    WriteTextFile(error_path, validation_error);
}

bool StripLegacyDecompilerBanner(std::string* source, std::string* note) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    std::vector<std::string_view> lines;
    std::size_t start = 0;
    while (start <= source->size()) {
        const std::size_t end = source->find('\n', start);
        if (end == std::string::npos) {
            lines.emplace_back(source->data() + start, source->size() - start);
            break;
        }
        lines.emplace_back(source->data() + start, end - start);
        start = end + 1;
        if (start == source->size()) {
            lines.emplace_back(source->data() + start, 0);
            break;
        }
    }

    std::size_t banner_lines = 0;
    while (banner_lines < lines.size()) {
        std::string_view trimmed = lines[banner_lines];
        while (!trimmed.empty() &&
               std::isspace(static_cast<unsigned char>(trimmed.front())) != 0) {
            trimmed.remove_prefix(1);
        }
        while (!trimmed.empty() &&
               std::isspace(static_cast<unsigned char>(trimmed.back())) != 0) {
            trimmed.remove_suffix(1);
        }
        if (trimmed.empty()) {
            ++banner_lines;
            continue;
        }
        if (StartsWith(trimmed, "-- Decompiled using ") ||
            StartsWith(trimmed, "-- Command line was: ")) {
            ++banner_lines;
            continue;
        }
        break;
    }

    if (banner_lines == 0) {
        return false;
    }

    std::size_t erase_through = 0;
    for (std::size_t i = 0; i < banner_lines; ++i) {
        erase_through += lines[i].size();
        if (erase_through < source->size() && (*source)[erase_through] == '\n') {
            ++erase_through;
        }
    }

    source->erase(0, erase_through);
    if (note != nullptr) {
        *note = JoinNotes(*note, "normalized_removed_legacy_decompiler_banner");
    }
    return true;
}

bool WriteNormalizedLuaTextFile(const std::filesystem::path& path,
                                std::string text,
                                std::string* note) {
    StripLegacyDecompilerBanner(&text, note);
    return WriteTextFile(path, text);
}

bool CopyNormalizedLuaTextFile(const std::filesystem::path& from,
                               const std::filesystem::path& to,
                               std::string* note) {
    std::string text;
    if (!ReadTextFile(from, text)) {
        return false;
    }
    return WriteNormalizedLuaTextFile(to, std::move(text), note);
}

} // namespace refii::lua::internal
