#pragma once

#include <string>
#include <vector>

namespace refii::lua::internal {

bool TryRepairEmptyNilGuardedAssignments(std::vector<std::string>* lines);

}  // namespace refii::lua::internal
