#pragma once

#include <filesystem>
#include <string>
#include <string_view>

namespace refii::lua::internal {

bool HasValidatedNativeRawNote(std::string_view note);
bool AllowsEmptyNormalizedLuaOutput(std::string_view note);
bool IsDisassemblyPlaceholderLuaText(std::string_view source);
bool IsDisassemblyPlaceholderLuaFile(const std::filesystem::path& path);
bool HasUsableDecompiledLuaOutput(const std::filesystem::path& path, std::string_view note);
std::string BuildDisassemblyPlaceholder(const std::filesystem::path& input_relative,
                                        const std::filesystem::path& disassembly_relative,
                                        std::string_view source_name);

}  // namespace refii::lua::internal
