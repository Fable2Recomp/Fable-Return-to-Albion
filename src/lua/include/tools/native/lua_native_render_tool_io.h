#pragma once

#include "tools/native/native_lua_decompiler.h"

#include <filesystem>
#include <string>
#include <string_view>

namespace refii::lua::internal {

void ResetNativeRenderToolIoCaches();
bool TryRenderNativeFunctionViaTool(const refii::lua::LuaWorkspaceDecompileOptions& options,
                                    const std::filesystem::path& input_path,
                                    std::size_t function_index,
                                    std::string* rendered_function,
                                    std::string* note);
void MaybeDumpInvalidNativeCandidate(std::string_view note_prefix,
                                     const std::filesystem::path* input_path,
                                     std::string_view source,
                                     std::string_view validation_error);
bool StripLegacyDecompilerBanner(std::string* source, std::string* note);
bool WriteNormalizedLuaTextFile(const std::filesystem::path& path,
                                std::string text,
                                std::string* note);

} // namespace refii::lua::internal
