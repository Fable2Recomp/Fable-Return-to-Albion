#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

bool TryRepairCandidateMismatchedFunctionsFromReference(
    const std::vector<char>& input_bytes,
    const std::string& reference_source,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix,
    bool require_reference_exact = false);

}  // namespace refii::lua::internal
