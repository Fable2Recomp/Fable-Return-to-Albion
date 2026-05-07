#pragma once

#include <cstdint>
#include <string>
#include <string_view>

namespace refii::lua::internal {

std::uint64_t ComputeLightweightSourceFingerprint(std::string_view text);
std::string BuildTopLevelReplacementState(std::string_view source,
                                          std::string_view validation_error);

}  // namespace refii::lua::internal
