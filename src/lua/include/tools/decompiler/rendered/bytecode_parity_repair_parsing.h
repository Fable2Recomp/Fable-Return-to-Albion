#pragma once

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

bool TryExtractBytecodeParityQuotedLiteral(std::string_view text,
                                           std::size_t* offset,
                                           std::string* literal);
bool TryExtractBytecodeParityQuotedLiteralPair(std::string_view note,
                                               std::string_view marker,
                                               std::string* expected_literal,
                                               std::string* actual_literal);
std::string StripBytecodeRepairWholeParens(std::string_view value);
bool IsBytecodeRepairSafeDottedPrefix(std::string_view value);
std::vector<std::string> SplitBytecodeRepairTopLevelArgs(std::string_view body);
bool IsBytecodeRepairSimpleCalleePath(std::string_view callee);
bool IsBytecodeRepairSimpleCallExpression(std::string_view expression);
bool BytecodeRepairSourceContainsToken(const std::vector<std::string>& source_lines,
                                       std::string_view token);
std::string BytecodeRepairCamelToSnake(std::string_view value);
std::string InferBytecodeRepairTempNameFromCall(std::string_view expression);
std::string ChooseBytecodeRepairTempName(const std::vector<std::string>& source_lines,
                                         std::string_view expression,
                                         bool prefer_call_name);

struct BytecodeRepairParsedTableField {
    std::string key;
    std::string value;
};

bool ParseBytecodeRepairSingleAssignment(std::string_view line,
                                         bool* is_local,
                                         std::string* lhs_out,
                                         std::string* rhs_out);
bool ParseBytecodeRepairPlainAssignment(std::string_view line,
                                        bool* is_local,
                                        std::string* lhs_out,
                                        std::string* rhs_out);
bool ParseBytecodeRepairAssignmentList(std::string_view line,
                                       bool* is_local_out,
                                       std::vector<std::string>* lhs_names_out,
                                       std::string* rhs_out);
bool ParseBytecodeRepairLocalInitializer(std::string_view line,
                                         std::string* lhs_out,
                                         std::string* rhs_out);
std::size_t FindBytecodeRepairTopLevelToken(std::string_view value,
                                            std::string_view token);
bool ParseBytecodeRepairNotOrNotAndCondition(std::string_view condition,
                                             std::string* positive_lhs,
                                             std::string* positive_rhs,
                                             std::string* carry_condition);
bool SplitBytecodeRepairTopLevelBooleanParts(std::string_view condition,
                                             std::string_view op,
                                             std::vector<std::string>* parts);
bool TryExtractBytecodeRepairIfCondition(std::string_view line,
                                         std::string* indent_out,
                                         std::string* condition_out);
std::size_t FindBytecodeRepairMatchingBrace(std::string_view line,
                                            std::size_t open_pos);
bool ParseBytecodeRepairTableLiteralFields(
    std::string_view literal,
    std::vector<BytecodeRepairParsedTableField>* fields_out);

}  // namespace refii::lua::internal
