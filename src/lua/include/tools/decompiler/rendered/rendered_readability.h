#pragma once

#include <cstddef>
#include <string>
#include <string_view>

namespace refii::lua::internal {

struct LuaReadabilitySummary {
    std::size_t nonempty_line_count = 0;
    std::size_t temporary_identifier_count = 0;
    std::size_t unique_temporary_identifier_count = 0;
    std::size_t temporary_leading_line_count = 0;
    std::size_t temporary_local_declaration_count = 0;
    std::size_t temporary_assignment_count = 0;
    std::size_t temporary_table_field_assignment_count = 0;
    std::size_t temporary_self_reference_assignment_count = 0;
    std::size_t multi_temporary_token_line_count = 0;
};

bool LooksLikeRegisterizedTemporaryName(std::string_view name);
bool LooksLikeTemporarySelfReferenceAssignment(std::string_view trimmed);
bool LooksLikeTemporaryTableFieldAssignment(std::string_view trimmed);
LuaReadabilitySummary SummarizeLuaReadability(std::string_view source);
std::size_t CountLikelyUndefinedTemporaryUses(std::string_view source);
std::size_t ScoreLuaReadabilityRisk(const LuaReadabilitySummary& summary);
bool LooksLikeRegisterizedLua(const LuaReadabilitySummary& summary);
bool ContainsNilArithmeticPattern(std::string_view line);
std::size_t CountObviouslyInvalidNilUsage(std::string_view source);
bool ContainsObviouslyInvalidNilUsage(std::string_view source);
bool IsSimpleLuaIdentityToken(std::string_view token);
bool TryNormalizeDisassemblyRecoveryInvalidNilArithmetic(std::string& source,
                                                         std::string* note);
bool IsRunnableLuaDespiteTemporaryNoise(const LuaReadabilitySummary& summary);
bool IsStructuredTableInitializerLuaDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary);
bool IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary);
bool IsConservativeSmallHoldoutQualityRelaxCandidate(
    const LuaReadabilitySummary& summary);
bool IsConservativeGamefaceQualityRelaxCandidate(
    const LuaReadabilitySummary& summary);
bool IsConservativeBehaviorTerminalQualityRelaxCandidate(
    const LuaReadabilitySummary& summary);
std::string DescribeLuaReadability(const LuaReadabilitySummary& summary);

}  // namespace refii::lua::internal
