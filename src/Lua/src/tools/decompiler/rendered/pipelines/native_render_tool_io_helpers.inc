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
    RenderedFunctionCacheEntry& cache_entry =
        g_lua_decompiler_session_cache.rendered_functions[cache_key];
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

bool ReadTextFile(const std::filesystem::path& path, std::string& text) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    text.assign(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
    return stream.good() || stream.eof();
}

bool WriteTextFile(const std::filesystem::path& path, std::string_view text) {
    if (path.has_parent_path()) {
        std::error_code create_error;
        const std::filesystem::path parent = path.parent_path();
        if (!parent.empty()) {
            std::filesystem::create_directories(parent, create_error);
            if (create_error) {
                return false;
            }
        }
    }

    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        return false;
    }

    stream.write(text.data(), static_cast<std::streamsize>(text.size()));
    return static_cast<bool>(stream);
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
