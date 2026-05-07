#pragma once

#include <string>
#include <vector>

namespace refii::lua::internal {

bool TryRepairTableBuilderAssignmentCarries(std::vector<std::string>* lines);

}  // namespace refii::lua::internal
