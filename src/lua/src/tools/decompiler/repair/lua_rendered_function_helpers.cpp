#include "tools/decompiler/repair/lua_rendered_function_helpers.h"

#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <cstddef>
#include <string>
#include <string_view>

namespace refii::lua::internal {

bool TryExtractRenderedFunctionHeaderParameters(std::string_view line,
                                                std::vector<std::string>* parameters) {
    if (parameters == nullptr) {
        return false;
    }

    const std::string_view trimmed =
        refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
    const std::size_t function_pos = trimmed.find("function");
    if (function_pos == std::string_view::npos) {
        return false;
    }

    const std::size_t params_begin = trimmed.find('(', function_pos);
    if (params_begin == std::string_view::npos) {
        return false;
    }

    int depth = 0;
    std::size_t params_end = std::string_view::npos;
    for (std::size_t index = params_begin; index < trimmed.size(); ++index) {
        if (trimmed[index] == '(') {
            ++depth;
        } else if (trimmed[index] == ')') {
            --depth;
            if (depth == 0) {
                params_end = index;
                break;
            }
        }
    }
    if (params_end == std::string_view::npos || params_end <= params_begin) {
        return false;
    }

    parameters->clear();
    const std::string_view params =
        trimmed.substr(params_begin + 1, params_end - params_begin - 1);
    std::size_t cursor = 0;
    while (cursor <= params.size()) {
        const std::size_t comma = params.find(',', cursor);
        const std::string_view part = refii::lua::TrimLuaWhitespace(
            comma == std::string_view::npos ? params.substr(cursor)
                                            : params.substr(cursor, comma - cursor));
        if (!part.empty() && part != "...") {
            if (!refii::lua::IsValidLuaIdentifier(part)) {
                parameters->clear();
                return false;
            }
            parameters->emplace_back(part);
        }
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }

    return true;
}

std::string RewriteLuaIdentifiersWithMap(
    std::string_view line,
    const std::unordered_map<std::string, std::string>& replacements) {
    if (replacements.empty()) {
        return std::string(line);
    }

    const LuaTokenScanOptions scan_options{
        .mode = LuaTokenScanMode::kCommentAware,
        .parse_quoted_literals = true,
        .use_stable_copy = false,
    };

    std::string rewritten;
    std::size_t cursor = 0;
    ForEachLuaIdentifierToken(
        line,
        scan_options,
        [&](std::string_view token, std::size_t begin, std::size_t end) {
            if (begin > cursor) {
                rewritten.append(line.substr(cursor, begin - cursor));
            }
            const auto it = replacements.find(std::string(token));
            if (it != replacements.end()) {
                rewritten += it->second;
            } else {
                rewritten.append(token);
            }
            cursor = end;
        });
    if (cursor < line.size()) {
        rewritten.append(line.substr(cursor));
    }
    return rewritten;
}

}  // namespace refii::lua::internal
