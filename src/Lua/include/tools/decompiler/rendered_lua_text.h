#pragma once

#include "tools/native_lua_chunk.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

bool IsLuaKeyword(std::string_view value);
bool IsValidLuaIdentifier(std::string_view value);
std::string EscapeLuaString(std::string_view value);
std::string RenderLuaNumber(double value);
std::string RenderLuaConstant(const Lua51Constant& constant);
std::string RenderLuaGlobal(std::string_view name);
std::string RenderLuaUpvalue(const Lua51Function& function, int index);

std::vector<std::string> SplitRenderedLines(std::string_view text);
std::string_view TrimLuaWhitespace(std::string_view value);
bool IsRenderTemporaryName(std::string_view value);
std::size_t FindIdentifierUse(std::string_view text,
                              std::string_view identifier,
                              std::size_t offset = 0);
std::size_t CountIdentifierUses(std::string_view text, std::string_view identifier);
bool IsTemporaryAssignmentLine(std::string_view line,
                               std::string* name,
                               std::string* expression);
bool IsLocalTemporaryAssignmentLine(std::string_view line,
                                    std::string* name,
                                    std::string* expression);
bool IsAssignmentToTemporary(std::string_view line, std::string_view identifier);
bool LocalizeRenderedLuaFirstAssignments(std::vector<std::string>* lines);
bool FindIdentifierUseInAssignmentRhs(std::string_view line,
                                      std::string_view identifier,
                                      std::size_t* absolute_found,
                                      std::size_t* use_count);
std::string WrapInlineExpression(std::string_view expression);
bool HasPositiveLuaExpressionBalance(std::string_view expression,
                                     int* paren_balance_out = nullptr,
                                     int* brace_balance_out = nullptr);

bool SimplifyRenderedLuaTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaReturnTemporaries(std::vector<std::string>* lines);
std::size_t CountIdentifierKeyword(std::string_view line, std::string_view keyword);
bool StartsWithLuaKeyword(std::string_view text, std::string_view keyword);
std::string_view StripLuaLineComment(std::string_view line);
int GetRenderedLuaBlockDelta(std::string_view line);
bool IsRenderedFunctionStartLine(std::string_view line);
std::size_t FindRenderedFunctionBlockEnd(const std::vector<std::string>& lines,
                                         std::size_t start_line);

bool ParseRenderedTableInitializerLine(std::string_view line,
                                       std::string* indent,
                                       std::string* lhs_prefix,
                                       std::string* name);
bool ParseRenderedTableLiteralStartLine(std::string_view line,
                                        std::string* indent,
                                        std::string* lhs_prefix,
                                        std::string* name);
bool ParseRenderedTableFieldAssignment(std::string_view line,
                                       std::string_view table_name,
                                       std::string* field_key,
                                       std::string* rhs_expression,
                                       bool* rhs_is_function,
                                       int* array_index);
bool ParseRenderedAssignmentLine(std::string_view line,
                                 bool* is_local,
                                 std::vector<std::string>* lhs_names,
                                 std::string* rhs_expression);
bool RemoveRenderedLuaEmptyIfBlocks(std::vector<std::string>* lines);
bool RemoveRenderedLuaNoOpAssignments(std::vector<std::string>* lines);
std::size_t FindRenderedTableLiteralEnd(const std::vector<std::string>& lines,
                                        std::size_t start_line);
bool LocalizeRenderedLuaFunctionLocals(std::vector<std::string>* lines);
bool RewriteTrailingRenderedTableBuilderTarget(std::vector<std::string>* lines,
                                               std::string_view table_name,
                                               std::string_view target_expression);
bool SimplifyRenderedLuaTableBuilders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaReturnedTableBuilders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTableInitializerTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaTableLiteralTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaAssignedFunctionTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaFunctionTemporaries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaConditionalIifes(std::vector<std::string>* lines);
bool SimplifyRenderedLuaConditionHeaders(std::vector<std::string>* lines);
bool SimplifyRenderedLuaExpressionParentheses(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDotAccess(std::vector<std::string>* lines);
bool SimplifyRenderedLuaCallParentheses(std::vector<std::string>* lines);
bool DisambiguateRenderedLuaLeadingIifeStatements(std::vector<std::string>* lines);
bool NormalizeRenderedLuaWhitespace(std::vector<std::string>* lines);

void AppendIndentedStatement(std::vector<std::string>* lines,
                             std::string_view indent,
                             std::string_view statement);

}  // namespace refii::lua
