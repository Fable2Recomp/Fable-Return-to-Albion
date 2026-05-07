#include "tools/decompiler/rendered/output_placeholder_helpers.h"

#include <fstream>
#include <string>

namespace refii::lua::internal {
namespace {

std::string_view TrimPlaceholderWhitespace(std::string_view value) {
    while (!value.empty() &&
           (value.front() == ' ' || value.front() == '\t' || value.front() == '\r' ||
            value.front() == '\n')) {
        value.remove_prefix(1);
    }
    while (!value.empty() &&
           (value.back() == ' ' || value.back() == '\t' || value.back() == '\r' ||
            value.back() == '\n')) {
        value.remove_suffix(1);
    }
    return value;
}

}  // namespace

bool HasValidatedNativeRawNote(std::string_view note) {
    return note.find("native_main_chunk_raw_validated") != std::string_view::npos ||
           note.find("native_first_raw_validated") != std::string_view::npos;
}

bool AllowsEmptyNormalizedLuaOutput(std::string_view note) {
    return note.find("normalized_removed_legacy_decompiler_banner") != std::string_view::npos ||
           note.find("empty_stub_body") != std::string_view::npos ||
           note.find("workspace_direct_readable_recovery_promoted_exact") !=
               std::string_view::npos;
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
    return TrimPlaceholderWhitespace(first_line) == "-- refii-disassembly-placeholder";
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

std::string BuildDisassemblyPlaceholder(const std::filesystem::path& input_relative,
                                        const std::filesystem::path& disassembly_relative,
                                        std::string_view source_name) {
    std::string placeholder;
    placeholder.reserve(512);
    placeholder += "-- refii-disassembly-placeholder\n";
    placeholder += "-- This script could not be decompiled into valid Lua yet.\n";
    placeholder += "-- Path: " + input_relative.generic_string() + "\n";
    if (!source_name.empty()) {
        placeholder += "-- Source: " + std::string(source_name) + "\n";
    }
    placeholder += "-- Readable disassembly: " + disassembly_relative.generic_string() + "\n";
    placeholder += "-- Replace this file with valid Lua to activate a loose-script override.\n";
    return placeholder;
}

}  // namespace refii::lua::internal
