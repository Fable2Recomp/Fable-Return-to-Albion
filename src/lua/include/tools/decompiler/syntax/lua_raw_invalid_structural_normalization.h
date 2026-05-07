#pragma once

#include <string>
#include <string_view>

namespace refii::lua::internal {

bool IsTargetedRawInvalidStructuralFamily(std::string_view validation_error);
bool TryApplyMethodLabelStructuralNormalization(std::string& source,
                                                std::string* note);
bool TryRewriteInlineTableLiteralFieldAssignments(std::string& source,
                                                  std::string* note);
bool TryRepairInlineTableFunctionFieldBoundaryBreakage(std::string& source,
                                                       std::string* note);
bool TryRepairInlineTableFunctionPrematureEndBreakage(std::string& source,
                                                      std::string* note);
bool TryStripSyntheticTopLevelFunctionWrapper(std::string& source,
                                              std::string* note);
bool TryNormalizeInvalidNilCallExpressions(std::string& source,
                                           std::string* note);
bool TryStripStandaloneEndBeforeTopLevelClassBoundary(std::string& source,
                                                      std::string* note);
bool TryApplyTargetedRawInvalidStructuralPreRepair(std::string& source,
                                                   std::string* validation_error,
                                                   std::string_view validation_label,
                                                   std::string* note);

}  // namespace refii::lua::internal
