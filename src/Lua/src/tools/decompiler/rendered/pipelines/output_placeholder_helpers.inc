bool HasValidatedNativeRawNote(std::string_view note) {
    return note.find("native_main_chunk_raw_validated") != std::string_view::npos ||
           note.find("native_first_raw_validated") != std::string_view::npos;
}

bool AllowsEmptyNormalizedLuaOutput(std::string_view note) {
    return note.find("normalized_removed_legacy_decompiler_banner") != std::string_view::npos ||
           note.find("empty_stub_body") != std::string_view::npos;
}

bool IsDisassemblyPlaceholderLuaText(std::string_view source) {
    constexpr std::string_view kPlaceholderMarker = "-- refii-disassembly-placeholder";
    return source.find(kPlaceholderMarker) != std::string_view::npos;
}

bool IsDisassemblyPlaceholderLuaFile(const std::filesystem::path& path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    std::string first_line;
    if (!std::getline(stream, first_line)) {
        return false;
    }
    return TrimWhitespace(first_line) == "-- refii-disassembly-placeholder";
}

bool HasUsableDecompiledLuaOutput(const std::filesystem::path& path, std::string_view note) {
    if (!std::filesystem::exists(path)) {
        return false;
    }

    std::error_code size_error;
    const auto size = std::filesystem::file_size(path, size_error);
    if (!size_error && size > 0) {
        return !IsDisassemblyPlaceholderLuaFile(path);
    }

    return AllowsEmptyNormalizedLuaOutput(note);
}
