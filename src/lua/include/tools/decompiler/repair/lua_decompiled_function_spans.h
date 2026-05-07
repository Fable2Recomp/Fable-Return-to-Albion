#pragma once

#include "tools/native/native_lua_chunk.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace refii::lua::internal {

struct DecompiledFunctionSpan {
    std::size_t start_line = 0;
    std::size_t end_line = 0;
    std::string prefix;
    std::string suffix;
    bool has_error = false;
    bool looks_suspicious = false;
    bool top_level = false;
};

bool IsTopLevelAnonymousBootstrapSpan(const DecompiledFunctionSpan& span);

bool SpanUsesTemporaryParameterNamesWhenChunkHasNamedParameters(
    const std::vector<std::string>& lines,
    const DecompiledFunctionSpan& span,
    const refii::lua::Lua51Function& function);

std::vector<DecompiledFunctionSpan> CollectDecompiledFunctionSpans(
    const std::vector<std::string>& lines);

bool TryInferClosureAssignmentPrefix(const refii::lua::Lua51Function& main_function,
                                     std::size_t function_index,
                                     std::string* prefix_out);

std::unordered_map<std::string, DecompiledFunctionSpan> BuildAssignedPrefixSpanMap(
    const std::vector<std::string>& lines,
    const std::vector<DecompiledFunctionSpan>& spans);

bool TryExtractAssignedPrefixFromSpanHeader(const std::vector<std::string>& lines,
                                            const DecompiledFunctionSpan& span,
                                            std::string* prefix_out);

bool TryExtractFunctionSpanByHeaderPrefix(const std::vector<std::string>& lines,
                                          std::string_view assigned_prefix,
                                          DecompiledFunctionSpan* span_out);

bool TryExtractTopLevelFunctionSpanByPrefixUntilNextBoundary(
    const std::vector<std::string>& lines,
    std::string_view assigned_prefix,
    DecompiledFunctionSpan* span_out);

bool TryFindFunctionSpanByTemporaryAssignmentPrefix(
    const std::vector<std::string>& lines,
    std::string_view assigned_prefix,
    DecompiledFunctionSpan* span_out);

bool TryFindFunctionSpanByPrefix(const std::vector<std::string>& lines,
                                 std::string_view assigned_prefix,
                                 DecompiledFunctionSpan* span_out);

bool TryResolveFunctionSpanForIndex(
    const refii::lua::Lua51Function& main_function,
    std::size_t preorder_function_index,
    std::size_t root_child_index,
    const std::vector<std::string>& lines,
    const std::vector<DecompiledFunctionSpan>& spans,
    const std::unordered_map<std::string, DecompiledFunctionSpan>* prefix_span_map,
    DecompiledFunctionSpan* span_out);

void AppendLuaFunctionChildrenPreorder(const refii::lua::Lua51Function& function,
                                       std::vector<const refii::lua::Lua51Function*>* out);

void AppendLuaFunctionChildrenPreorderWithRootChildIndex(
    const refii::lua::Lua51Function& function,
    std::size_t inherited_root_child_index,
    std::vector<const refii::lua::Lua51Function*>* functions_out,
    std::vector<std::size_t>* root_child_indices_out);

}  // namespace refii::lua::internal
