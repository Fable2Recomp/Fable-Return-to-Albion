#pragma once

#include <filesystem>
#include <string>
#include <string_view>

namespace refii::lua::internal {

bool ReadTextFile(const std::filesystem::path& path, std::string& text);
bool WriteTextFile(const std::filesystem::path& path, std::string_view text);

}  // namespace refii::lua::internal
