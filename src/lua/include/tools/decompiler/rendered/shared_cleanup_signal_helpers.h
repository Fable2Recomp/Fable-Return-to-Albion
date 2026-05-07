#pragma once

#include <string>
#include <vector>

namespace refii::lua::internal {

bool HasQuestLikeCleanupSignals(const std::vector<std::string>& lines);
bool HasTableAliasCleanupSignals(const std::vector<std::string>& lines);
bool ApplyQuestStructuralCleanupIfRelevant(std::vector<std::string>* lines);
bool ApplyQualifiedTableBuilderCleanupIfRelevant(std::vector<std::string>* lines);

}  // namespace refii::lua::internal
