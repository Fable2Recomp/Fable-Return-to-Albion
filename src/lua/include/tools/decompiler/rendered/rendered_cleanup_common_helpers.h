#pragma once

#include <cstddef>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace refii::lua {

bool IsRenderedSetlistErrorBanner(std::string_view line);
bool IsRenderedSetlistMissingListBanner(std::string_view line);
void CollectRenderedTemporaryNames(std::string_view expression,
                                   std::unordered_set<std::string>* names);
bool HasRenderedTemporaryAssignmentInRange(const std::vector<std::string>& lines,
                                           std::size_t begin,
                                           std::size_t end,
                                           std::string_view temporary_name);
int ComputeRenderedBraceDeltaIgnoringStringsAndComments(std::string_view line);
bool RenderedLineBeginsScopedBlock(std::string_view trimmed);
bool RenderedLineBeginsLoopBlock(std::string_view trimmed);
void ComputeRenderedScopePaths(const std::vector<std::string>& lines,
                               std::vector<std::vector<std::size_t>>* scope_paths);
bool IsRenderedScopePathPrefix(const std::vector<std::size_t>& prefix,
                               const std::vector<std::size_t>& candidate);
bool IsRenderedSyntheticRootWrapperFunction(const std::vector<std::string>& lines,
                                            std::size_t function_start,
                                            std::size_t function_end);
std::size_t FindEnclosingRenderedFunctionBlockBegin(const std::vector<std::string>& lines,
                                                    std::size_t line_index);
bool ParseRenderedLocalDeclarationLine(std::string_view line,
                                       std::vector<std::string>* names);
bool IsRenderedShadowingLocalDeclaration(std::string_view line, std::string_view identifier);
void SeedRenderedFunctionParameters(std::string_view line,
                                    std::unordered_set<std::string>* seen_names);
bool ShouldEmitLocalForRenderedIdentifierTarget(const std::vector<std::string>& lines,
                                                std::size_t line_index,
                                                std::string_view target_expression);
bool LooksLikeRenderedNumericLiteral(std::string_view value);
bool FindRenderedMatchingParen(std::string_view text,
                               std::size_t open_index,
                               std::size_t* close_index);
std::string StripRenderedWholeParentheses(std::string_view value, bool* changed);
bool SplitRenderedTopLevelBooleanChain(std::string_view value,
                                       std::string_view keyword,
                                       std::vector<std::string>* parts);
bool IsRenderedSafePrefixExpression(std::string_view candidate);
bool IsRenderedSimpleMemberPath(std::string_view candidate);
bool ParseRenderedCreateSubclassExpression(std::string_view text,
                                           std::string* base_expression,
                                           std::string* subclass_name);
bool ParseRenderedCreateSubclassStatement(std::string_view line,
                                          std::string* base_expression,
                                          std::string* subclass_name);

}  // namespace refii::lua
