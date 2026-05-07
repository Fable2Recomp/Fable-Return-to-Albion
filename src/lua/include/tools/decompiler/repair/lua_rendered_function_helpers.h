#pragma once

#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace refii::lua::internal {

bool TryExtractRenderedFunctionHeaderParameters(std::string_view line,
                                                std::vector<std::string>* parameters);
std::string RewriteLuaIdentifiersWithMap(
    std::string_view line,
    const std::unordered_map<std::string, std::string>& replacements);

}  // namespace refii::lua::internal
