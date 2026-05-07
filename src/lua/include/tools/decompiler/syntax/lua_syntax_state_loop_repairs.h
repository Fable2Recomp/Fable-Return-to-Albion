#pragma once

#include <string>
#include <string_view>

namespace refii::lua::internal {

bool IsLargeChunkSyntaxRepairCandidate(std::string_view validation_error);
bool TryRewriteRepeatStateLadderLoops(std::string& decompiled_source,
                                      std::string* note);
bool TryRewriteRepeatLeadingStateLoops(std::string& decompiled_source,
                                       std::string* note);
bool TryRewriteRepeatStateMachineLoops(std::string& decompiled_source,
                                       std::string* note);
bool TryRewriteRepeatMonitorLoopsWithoutUntil(std::string& decompiled_source,
                                              std::string* note);
bool TryLiftStateChainAfterDoReturnEnd(std::string& decompiled_source,
                                       std::string* note);
bool TryLiftStateContinuationAfterWaitLoop(std::string& decompiled_source,
                                           std::string* note);

}  // namespace refii::lua::internal
