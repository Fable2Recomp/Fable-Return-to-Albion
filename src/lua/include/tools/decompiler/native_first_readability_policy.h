#pragma once

#include "tools/decompiler/native_first.h"

#include <cstddef>
#include <string>

namespace refii::lua::decompiler {

std::size_t ScoreLuaReadabilityRisk(const NativeLuaReadabilitySummary& summary);
bool LooksLikeRegisterizedLua(const NativeLuaReadabilitySummary& summary);
bool IsRunnableLuaDespiteTemporaryNoise(const NativeLuaReadabilitySummary& summary);
bool IsStructuredTableInitializerLuaDespiteTemporaryNoise(
    const NativeLuaReadabilitySummary& summary);
bool IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
    const NativeLuaReadabilitySummary& summary);
bool ShouldTrySeedRepairForValidatedCandidate(const NativeLuaReadabilitySummary& clean_summary);
bool ShouldTryShadowRepairForValidatedCandidate(const NativeLuaReadabilitySummary& clean_summary,
                                                const NativeLuaReadabilitySummary& raw_summary);
bool ShouldTryExpandedAssignmentRepairForValidatedCandidate(
    const NativeLuaReadabilitySummary& clean_summary);
bool ShouldTryGuardedCarryRepairForValidatedCandidate(
    const NativeLuaReadabilitySummary& raw_summary);
std::string DescribeLuaReadability(const NativeLuaReadabilitySummary& summary);
bool IsPreferredValidatedCandidate(const NativeMainChunkCandidate& candidate,
                                   const NativeMainChunkCandidate& current_best);

}  // namespace refii::lua::decompiler
