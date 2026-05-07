#pragma once

#include <string>
#include <string_view>

namespace refii::lua::internal {

bool TryRewriteForGeneratorElseArtifacts(std::string& decompiled_source,
                                         std::string* note);
bool TryCommentForGeneratorArtifacts(std::string& decompiled_source,
                                     std::string* note);
bool LooksUnsafeToCommentForSyntaxRepair(std::string_view line);
bool TryCommentReportedSyntaxLine(std::string& decompiled_source,
                                  std::string_view validation_error,
                                  std::string* note);

}  // namespace refii::lua::internal
