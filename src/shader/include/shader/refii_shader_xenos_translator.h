#pragma once

#include <filesystem>
#include <string>

namespace refii::shader {

bool TranslateXenosShaderBlob(
    const std::filesystem::path& input,
    const std::string& target,
    const std::string& stage,
    const std::string& name,
    const std::filesystem::path& output,
    const std::filesystem::path& log_path = std::filesystem::path{},
    std::string* error = nullptr);

}  // namespace refii::shader
