#include "tools/decompiler/native_first.h"

#include "tools/decompiler/rendered_lua_text.h"
#include "tools/native_lua_chunk.h"
#include "tools/native_lua_unluac.h"

#include <string_view>
#include <unordered_set>

#ifdef __cplusplus
extern "C" {
#endif
#include "lauxlib.h"
#include "lua.h"
#ifdef __cplusplus
}
#endif

namespace refii::lua::decompiler {
namespace {

constexpr std::string_view kLuaSignature = "\x1bLua";
constexpr std::size_t kPreferRawFirstInstructionThreshold = 12000;

struct LuaReadabilitySummary {
    std::size_t nonempty_line_count = 0;
    std::size_t temporary_identifier_count = 0;
    std::size_t unique_temporary_identifier_count = 0;
    std::size_t temporary_leading_line_count = 0;
    std::size_t temporary_local_declaration_count = 0;
    std::size_t temporary_assignment_count = 0;
    std::size_t temporary_table_field_assignment_count = 0;
    std::size_t temporary_self_reference_assignment_count = 0;
    std::size_t multi_temporary_token_line_count = 0;
};

std::size_t CountLuaFunctionInstructionsRecursive(const Lua51Function& function) {
    std::size_t total = function.code.size();
    for (const Lua51Function& child : function.child_functions) {
        total += CountLuaFunctionInstructionsRecursive(child);
    }
    return total;
}

bool IsLuaBytecode(const std::vector<char>& bytes) {
    return bytes.size() >= kLuaSignature.size() &&
           std::string_view(bytes.data(), kLuaSignature.size()) == kLuaSignature;
}

std::string ValidateLuaSource(std::string_view source, std::string_view chunk_name) {
    lua_State* state = luaL_newstate();
    if (state == nullptr) {
        return "validator_init_failed";
    }

    const std::string name(chunk_name.empty() ? "native_main_chunk" : chunk_name);
    const int status = luaL_loadbuffer(state,
                                       source.data(),
                                       source.size(),
                                       name.c_str());
    if (status == 0) {
        lua_close(state);
        return {};
    }

    const char* message = lua_tostring(state, -1);
    std::string error = message != nullptr ? message : "syntax_invalid";
    lua_close(state);
    return error;
}

bool IsIdentifierChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_';
}

std::string_view ExtractLeadingIdentifier(std::string_view trimmed) {
    if (trimmed.empty()) {
        return {};
    }

    const unsigned char first = static_cast<unsigned char>(trimmed.front());
    if ((std::isalpha(first) == 0) && trimmed.front() != '_') {
        return {};
    }

    std::size_t index = 1;
    while (index < trimmed.size() && IsIdentifierChar(trimmed[index])) {
        ++index;
    }
    return trimmed.substr(0, index);
}

std::string_view ExtractLocalDeclarationName(std::string_view trimmed) {
    if (!trimmed.starts_with("local ")) {
        return {};
    }

    trimmed = TrimLuaWhitespace(trimmed.substr(6));
    if (trimmed.empty()) {
        return {};
    }

    const std::string_view name = ExtractLeadingIdentifier(trimmed);
    if (name.empty()) {
        return {};
    }

    if (name.size() < trimmed.size()) {
        const char next = trimmed[name.size()];
        if (next != '=' && next != ',' && std::isspace(static_cast<unsigned char>(next)) == 0) {
            return {};
        }
    }

    return name;
}

template <typename Callback>
void ForEachLuaToken(std::string_view line, Callback&& callback) {
    for (std::size_t i = 0; i < line.size();) {
        const unsigned char ch = static_cast<unsigned char>(line[i]);
        if ((std::isalpha(ch) == 0) && line[i] != '_') {
            ++i;
            continue;
        }

        const std::size_t start = i;
        ++i;
        while (i < line.size() && IsIdentifierChar(line[i])) {
            ++i;
        }

        callback(line.substr(start, i - start));
    }
}

bool LooksLikeTemporarySelfReferenceAssignment(std::string_view trimmed) {
    const std::string_view lhs = ExtractLeadingIdentifier(trimmed);
    if (!IsRenderTemporaryName(lhs)) {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (!rhs.starts_with(lhs) || rhs.size() == lhs.size()) {
        return false;
    }

    const char next = rhs[lhs.size()];
    return next == '(' || next == '[' || next == '.' || next == ':';
}

bool LooksLikeTemporaryTableFieldAssignment(std::string_view trimmed) {
    const std::string_view lhs = ExtractLeadingIdentifier(trimmed);
    if (!IsRenderTemporaryName(lhs) || trimmed.size() <= lhs.size()) {
        return false;
    }

    const char next = trimmed[lhs.size()];
    if (next != '[' && next != '.') {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    return equal != std::string_view::npos && equal > lhs.size();
}

LuaReadabilitySummary SummarizeLuaReadability(std::string_view source) {
    LuaReadabilitySummary summary;
    const std::vector<std::string> lines = SplitRenderedLines(source);
    std::unordered_set<std::string> unique_temporaries;

    for (const std::string& raw_line : lines) {
        const std::string_view stripped = StripLuaLineComment(raw_line);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        ++summary.nonempty_line_count;

        std::size_t temporary_tokens_on_line = 0;
        ForEachLuaToken(stripped, [&](std::string_view token) {
            if (!IsRenderTemporaryName(token)) {
                return;
            }
            ++summary.temporary_identifier_count;
            ++temporary_tokens_on_line;
            unique_temporaries.emplace(token);
        });

        if (temporary_tokens_on_line >= 2) {
            ++summary.multi_temporary_token_line_count;
        }

        const std::string_view local_name = ExtractLocalDeclarationName(trimmed);
        if (!local_name.empty() && IsRenderTemporaryName(local_name)) {
            ++summary.temporary_local_declaration_count;
            ++summary.temporary_leading_line_count;
        } else {
            const std::string_view leading = ExtractLeadingIdentifier(trimmed);
            if (!leading.empty() && IsRenderTemporaryName(leading)) {
                ++summary.temporary_leading_line_count;
                if (trimmed.find('=') != std::string_view::npos) {
                    ++summary.temporary_assignment_count;
                }
                if (LooksLikeTemporaryTableFieldAssignment(trimmed)) {
                    ++summary.temporary_table_field_assignment_count;
                }
            }
        }

        if (LooksLikeTemporarySelfReferenceAssignment(trimmed)) {
            ++summary.temporary_self_reference_assignment_count;
        }
    }

    summary.unique_temporary_identifier_count = unique_temporaries.size();
    return summary;
}

std::size_t ScoreLuaReadabilityRisk(const LuaReadabilitySummary& summary) {
    return summary.temporary_leading_line_count * 5 +
           summary.temporary_local_declaration_count * 4 +
           summary.temporary_assignment_count * 3 +
           summary.temporary_self_reference_assignment_count * 8 +
           summary.multi_temporary_token_line_count * 2 +
           summary.unique_temporary_identifier_count;
}

bool LooksLikeRegisterizedLua(const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 10) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count >= 3 &&
        summary.temporary_leading_line_count * 2 >= summary.nonempty_line_count) {
        return true;
    }

    if (summary.temporary_leading_line_count >= 8 &&
        summary.multi_temporary_token_line_count >= 6 &&
        ScoreLuaReadabilityRisk(summary) >= 48) {
        return true;
    }

    return summary.nonempty_line_count >= 24 &&
           summary.temporary_leading_line_count * 3 >= summary.nonempty_line_count &&
           summary.unique_temporary_identifier_count >= 4 &&
           ScoreLuaReadabilityRisk(summary) >= 64;
}

bool IsRunnableLuaDespiteTemporaryNoise(const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 200) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    return summary.temporary_leading_line_count * 5 < summary.nonempty_line_count &&
           summary.multi_temporary_token_line_count * 6 < summary.nonempty_line_count &&
           summary.temporary_local_declaration_count * 16 < summary.nonempty_line_count;
}

bool IsStructuredTableInitializerLuaDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 80) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0 ||
        summary.temporary_table_field_assignment_count < 24 ||
        summary.temporary_local_declaration_count * 24 >= summary.nonempty_line_count ||
        summary.multi_temporary_token_line_count * 4 >= summary.nonempty_line_count) {
        return false;
    }

    if (summary.temporary_table_field_assignment_count * 2 >=
        summary.temporary_leading_line_count) {
        return true;
    }

    return summary.nonempty_line_count >= 300 &&
           summary.temporary_assignment_count * 2 >=
               summary.temporary_leading_line_count &&
           summary.unique_temporary_identifier_count <= 12;
}

bool IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 240) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0 ||
        summary.unique_temporary_identifier_count > 24 ||
        summary.temporary_leading_line_count * 8 >= summary.nonempty_line_count * 5) {
        return false;
    }

    const bool table_heavy = summary.temporary_table_field_assignment_count >= 20;
    const bool assignment_heavy = summary.temporary_assignment_count * 3 >=
                                  summary.temporary_leading_line_count * 2;
    return table_heavy || assignment_heavy;
}

bool ShouldTryRawAlternativeForValidatedCandidate(std::string_view source) {
    const LuaReadabilitySummary summary = SummarizeLuaReadability(source);
    return LooksLikeRegisterizedLua(summary) &&
           !IsRunnableLuaDespiteTemporaryNoise(summary) &&
           !IsStructuredTableInitializerLuaDespiteTemporaryNoise(summary) &&
           !IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(summary);
}

bool TryRenderCandidate(const Lua51Function& function,
                        std::string_view chunk_name,
                        bool use_raw_renderer,
                        NativeMainChunkCandidate* candidate) {
    if (candidate == nullptr) {
        return false;
    }

    std::string rendered;
    std::string render_error;
    const bool rendered_ok =
        use_raw_renderer
            ? RenderLua51StraightLineFunctionBodyRaw(function, &rendered, &render_error)
            : RenderLua51StraightLineFunctionBody(function, &rendered, &render_error);
    if (!rendered_ok) {
        candidate->note =
            (use_raw_renderer ? "native_first_raw_render_failed=" : "native_first_render_failed=") +
            render_error;
        return false;
    }

    if (rendered.empty() && !function.code.empty()) {
        candidate->note =
            use_raw_renderer ? "native_first_raw_render_failed=empty_body"
                             : "native_first_render_failed=empty_body";
        return false;
    }

    const std::string validation_error = ValidateLuaSource(rendered, chunk_name);
    candidate->source = std::move(rendered);
    candidate->used_raw_renderer = use_raw_renderer;
    candidate->syntax_validated = validation_error.empty();
    candidate->note = candidate->syntax_validated
                          ? (use_raw_renderer ? "native_first_raw_validated"
                                              : "native_first_validated")
                          : (use_raw_renderer ? "native_first_raw_invalid="
                                              : "native_first_invalid=") +
                                validation_error;
    return true;
}

}  // namespace

bool TryRenderValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                       std::string_view chunk_name,
                                       NativeMainChunkCandidate* candidate) {
    if (candidate == nullptr) {
        return false;
    }

    *candidate = {};
    if (!IsLuaBytecode(input_bytes)) {
        candidate->note = "native_first_non_bytecode";
        return false;
    }

    Lua51Chunk chunk;
    std::string parse_error;
    if (!ParseLua51Chunk(input_bytes, &chunk, &parse_error)) {
        candidate->note = parse_error.empty() ? "native_first_parse_failed"
                                              : "native_first_parse_failed=" + parse_error;
        return false;
    }

    const bool prefer_raw_first =
        CountLuaFunctionInstructionsRecursive(chunk.main) >
        kPreferRawFirstInstructionThreshold;
    NativeMainChunkCandidate best_valid_candidate;
    bool have_valid_candidate = false;
    NativeMainChunkCandidate best_invalid_candidate;
    bool have_invalid_candidate = false;
    const auto try_candidate = [&](bool use_raw_renderer) {
        NativeMainChunkCandidate rendered_candidate;
        if (!TryRenderCandidate(chunk.main, chunk_name, use_raw_renderer, &rendered_candidate)) {
            return false;
        }

        if (rendered_candidate.syntax_validated) {
            if (use_raw_renderer ||
                !ShouldTryRawAlternativeForValidatedCandidate(rendered_candidate.source)) {
                *candidate = std::move(rendered_candidate);
                return true;
            }

            if (!have_valid_candidate ||
                (best_valid_candidate.used_raw_renderer && !rendered_candidate.used_raw_renderer)) {
                best_valid_candidate = std::move(rendered_candidate);
                have_valid_candidate = true;
            }
            return false;
        }

        if (!have_invalid_candidate ||
            (best_invalid_candidate.used_raw_renderer && !rendered_candidate.used_raw_renderer)) {
            best_invalid_candidate = std::move(rendered_candidate);
            have_invalid_candidate = true;
        }
        return false;
    };

    if (prefer_raw_first && try_candidate(true)) {
        return true;
    }

    if (try_candidate(false)) {
        return true;
    }

    if (try_candidate(true)) {
        return true;
    }

    if (have_valid_candidate) {
        *candidate = std::move(best_valid_candidate);
        return true;
    }

    if (have_invalid_candidate) {
        *candidate = std::move(best_invalid_candidate);
        return true;
    }

    return false;
}

}  // namespace refii::lua::decompiler
