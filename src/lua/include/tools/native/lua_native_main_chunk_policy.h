#pragma once

#include "tools/decompiler/lua51_analysis.h"

#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

bool IsLargeNativeMainChunkInProcessCandidate(const std::vector<char>& input_bytes);
bool IsNativeMainChunkOutlierCandidate(const std::vector<char>& input_bytes);
bool IsTriviallyEmptyLuaMainChunk(const refii::lua::Lua51Function& function);
bool IsTriviallyEmptyLuaMainChunk(const std::vector<char>& input_bytes);
bool ShouldForceTimedNativeMainChunkSubprocess(const std::vector<char>& input_bytes);
bool IsCanonicalNativeDebugBankInputPath(const std::filesystem::path& input_path);
bool ShouldPreferInProcessNativeMainChunkForCanonicalHoldout(
    const std::vector<char>& input_bytes,
    const std::filesystem::path& input_path);
bool ShouldUseNativeMainChunkSubprocess(const std::vector<char>& input_bytes);
bool ShouldAllowInProcessNativeMainChunkFallback(const std::vector<char>& input_bytes);
bool ShouldPreferRawNativeMainChunkCandidate(const std::vector<char>& input_bytes);
unsigned int GetNativeMainChunkRenderTimeoutSeconds(const std::vector<char>& input_bytes,
                                                    unsigned int default_timeout_seconds);
bool IsEligibleLargeValidatedFastFinalize(std::string_view candidate_source,
                                          std::string_view note,
                                          std::string_view note_prefix);
std::filesystem::path MakeNativeMainChunkTempOutputPath(const std::filesystem::path& input_path);

} // namespace refii::lua::internal
