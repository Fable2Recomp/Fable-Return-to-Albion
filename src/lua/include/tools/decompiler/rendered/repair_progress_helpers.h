#pragma once

#include "tools/decompiler/lua_decompiler_internal.h"

#include <cstddef>
#include <string_view>

namespace refii::lua::internal {

std::size_t ExtractLuaOpenConstructLine(std::string_view validation_error);
bool IsUnclosedConstructValidationFamily(std::string_view raw_error);
bool IsBytecodeProvenValidationProgress(
    std::string_view baseline_error,
    std::string_view candidate_error,
    const LuaOutputQualitySummary& baseline_quality,
    const LuaOutputQualitySummary& candidate_quality);
bool IsSameFamilyQualityProgress(std::string_view baseline_source,
                                 std::string_view baseline_error,
                                 const LuaOutputQualitySummary& baseline_quality,
                                 std::string_view candidate_source,
                                 std::string_view candidate_error,
                                 const LuaOutputQualitySummary& candidate_quality);

}  // namespace refii::lua::internal
