#include "tools/decompiler/rendered/repair_progress_helpers.h"

#include "tools/decompiler/rendered/repair_state_keys.h"

#include <cctype>
#include <cstdlib>
#include <string>

namespace refii::lua::internal {

std::size_t ExtractLuaOpenConstructLine(std::string_view validation_error) {
    constexpr std::string_view kNeedle = "to close '";
    const std::size_t needle = validation_error.find(kNeedle);
    if (needle == std::string_view::npos) {
        return 0;
    }

    const std::size_t construct_end =
        validation_error.find("' at line ", needle + kNeedle.size());
    if (construct_end == std::string_view::npos) {
        return 0;
    }

    const std::size_t number_begin = construct_end + std::string_view("' at line ").size();
    std::size_t number_end = number_begin;
    while (number_end < validation_error.size() &&
           std::isdigit(static_cast<unsigned char>(validation_error[number_end])) != 0) {
        ++number_end;
    }
    if (number_end == number_begin) {
        return 0;
    }

    return static_cast<std::size_t>(std::strtoul(
        std::string(validation_error.substr(number_begin, number_end - number_begin)).c_str(),
        nullptr,
        10));
}

bool IsUnclosedConstructValidationFamily(std::string_view raw_error) {
    const std::string summary = SummarizeSyntaxError(raw_error);
    return StartsWith(summary, "'end'_expected_(to_close_'") ||
           StartsWith(summary, "'until'_expected_");
}

bool IsBytecodeProvenValidationProgress(
    std::string_view baseline_error,
    std::string_view candidate_error,
    const LuaOutputQualitySummary& baseline_quality,
    const LuaOutputQualitySummary& candidate_quality) {
    if (baseline_error.empty() || candidate_error.empty()) {
        return false;
    }
    if (!IsUnclosedConstructValidationFamily(baseline_error) ||
        !IsUnclosedConstructValidationFamily(candidate_error)) {
        return false;
    }
    if (!IsLuaOutputQualityNoWorse(baseline_quality, candidate_quality)) {
        return false;
    }

    const std::size_t baseline_open_line = ExtractLuaOpenConstructLine(baseline_error);
    const std::size_t candidate_open_line = ExtractLuaOpenConstructLine(candidate_error);
    if (baseline_open_line != 0 && candidate_open_line != 0 &&
        baseline_open_line != candidate_open_line) {
        return true;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(baseline_error);
    const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
    return baseline_descriptor.summary != candidate_descriptor.summary;
}

bool IsSameFamilyQualityProgress(std::string_view baseline_source,
                                 std::string_view baseline_error,
                                 const LuaOutputQualitySummary& baseline_quality,
                                 std::string_view candidate_source,
                                 std::string_view candidate_error,
                                 const LuaOutputQualitySummary& candidate_quality) {
    if (!IsLuaOutputQualityBetter(baseline_quality, candidate_quality)) {
        return false;
    }

    return BuildRepairFamilyStateKey(baseline_source, baseline_error) ==
           BuildRepairFamilyStateKey(candidate_source, candidate_error);
}

}  // namespace refii::lua::internal
