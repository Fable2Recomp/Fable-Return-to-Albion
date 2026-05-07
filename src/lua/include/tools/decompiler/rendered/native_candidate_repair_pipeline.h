#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

bool TryRepairNativeCandidateSource(std::string& candidate_source,
                                    const std::vector<char>* input_bytes,
                                    std::string* note,
                                    std::string_view note_prefix,
                                    std::string* validation_error_out);

}  // namespace refii::lua::internal
