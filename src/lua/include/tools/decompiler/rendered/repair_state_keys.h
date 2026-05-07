#pragma once

#include <string>
#include <string_view>

namespace refii::lua::internal {

std::string BuildRepairProgressStateKey(std::string_view source,
                                        std::string_view validation_error);
std::string BuildRepairQualityStateKey(std::string_view source,
                                       std::string_view validation_error);
std::string NormalizeRepairFamilySummary(std::string_view summary);
std::string BuildRepairFamilyStateKey(std::string_view source,
                                      std::string_view validation_error);

}  // namespace refii::lua::internal
