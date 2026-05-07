#include "tools/decompiler/repair/lua_local_diff_exactifier.h"

#include "tools/decompiler/native_first.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/decompiler/repair/lua_rendered_function_helpers.h"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace refii::lua::internal {

std::string_view ExtractLocalDiffMismatchNote(std::string_view note) {
    constexpr std::string_view kPrefix = "local_diff idx=";
    if (StartsWith(note, kPrefix)) {
        return note;
    }

    const std::size_t local_diff_pos = note.find(kPrefix);
    if (local_diff_pos == std::string_view::npos) {
        return {};
    }

    const std::size_t note_end = note.find(']', local_diff_pos);
    if (note_end == std::string_view::npos) {
        return refii::lua::TrimLuaWhitespace(note.substr(local_diff_pos));
    }
    return refii::lua::TrimLuaWhitespace(
        note.substr(local_diff_pos, note_end - local_diff_pos));
}

bool TryExactifyLocalDiffHeaderParameters(
    const refii::lua::audit::LuaBytecodeParityReference& parity_reference,
    std::string_view chunk_name,
    std::string* source,
    refii::lua::audit::LuaBytecodeParityResult* parity_out,
    std::string* note,
    std::string_view note_prefix,
    const std::vector<char>* input_bytes) {
    if (source == nullptr || parity_out == nullptr || parity_out->exact_bytecode ||
        !parity_out->first_mismatch.available ||
        std::llabs(parity_out->first_mismatch.instruction_delta) != 0 ||
        std::llabs(parity_out->first_mismatch.constant_delta) != 0 ||
        std::llabs(parity_out->first_mismatch.local_delta) != 0) {
        return false;
    }

    std::string mismatch_summary;
    std::string_view local_diff_note =
        ExtractLocalDiffMismatchNote(parity_out->first_mismatch.note);
    if (local_diff_note.empty()) {
        mismatch_summary = refii::lua::audit::DescribeMismatchForSummary(
            parity_out->first_mismatch);
        local_diff_note = ExtractLocalDiffMismatchNote(mismatch_summary);
        if (local_diff_note.empty()) {
            return false;
        }
    }

    const auto parse_local_name =
        [](std::string_view text, std::string* name_out) -> bool {
            if (name_out == nullptr) {
                return false;
            }
            const std::size_t at = text.find('@');
            if (at == std::string_view::npos || at == 0) {
                return false;
            }
            const std::string_view name =
                refii::lua::TrimLuaWhitespace(text.substr(0, at));
            if (!refii::lua::IsValidLuaIdentifier(name) ||
                refii::lua::IsRenderTemporaryName(name)) {
                return false;
            }
            *name_out = std::string(name);
            return true;
        };

    constexpr std::string_view kPrefix = "local_diff idx=";
    if (!StartsWith(local_diff_note, kPrefix)) {
        return false;
    }
    const std::size_t index_begin = kPrefix.size();
    std::size_t index_end = index_begin;
    while (index_end < local_diff_note.size() &&
           std::isdigit(static_cast<unsigned char>(local_diff_note[index_end])) != 0) {
        ++index_end;
    }
    if (index_end == index_begin) {
        return false;
    }

    const std::string_view remaining =
        refii::lua::TrimLuaWhitespace(local_diff_note.substr(index_end));
    const std::size_t arrow = remaining.find(" -> ");
    if (arrow == std::string_view::npos || arrow == 0 || arrow + 4 >= remaining.size()) {
        return false;
    }

    std::string expected_name;
    std::string candidate_name;
    if (!parse_local_name(remaining.substr(0, arrow), &expected_name) ||
        !parse_local_name(remaining.substr(arrow + 4), &candidate_name) ||
        expected_name == candidate_name || !StartsWith(candidate_name, "arg") ||
        StartsWith(expected_name, "arg")) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext source_context(*source);
    const std::vector<std::string>& lines = source_context.EnsureLines();
    const bool had_trailing_newline = source_context.HadTrailingNewline();
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (!refii::lua::IsRenderedFunctionStartLine(lines[line_index])) {
            continue;
        }

        std::vector<std::string> function_params;
        if (!TryExtractRenderedFunctionHeaderParameters(lines[line_index], &function_params) ||
            function_params.empty()) {
            continue;
        }
        if (std::find(function_params.begin(), function_params.end(), expected_name) !=
                function_params.end() ||
            std::find(function_params.begin(), function_params.end(), candidate_name) ==
                function_params.end()) {
            continue;
        }

        std::string_view stripped =
            refii::lua::StripLuaLineComment(lines[line_index]);
        const std::size_t function_pos = stripped.find("function");
        const std::size_t open_paren = stripped.find('(', function_pos);
        const std::size_t close_paren = stripped.rfind(')');
        if (function_pos == std::string_view::npos || open_paren == std::string_view::npos ||
            close_paren == std::string_view::npos || close_paren <= open_paren) {
            continue;
        }

        for (std::string& parameter : function_params) {
            if (parameter == candidate_name) {
                parameter = expected_name;
                break;
            }
        }

        std::string rewritten_param_text;
        for (std::size_t i = 0; i < function_params.size(); ++i) {
            if (i != 0) {
                rewritten_param_text += ", ";
            }
            rewritten_param_text += function_params[i];
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index].replace(open_paren + 1,
                                              close_paren - open_paren - 1,
                                              rewritten_param_text);
        const std::string transformed_source = JoinLines(transformed_lines, had_trailing_newline);
        if (!ValidateLuaSource(transformed_source, chunk_name).empty()) {
            continue;
        }

        refii::lua::audit::LuaBytecodeParityResult parity_after;
        if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                               transformed_source,
                                                               chunk_name,
                                                               &parity_after) &&
            parity_after.exact_bytecode) {
            *source = transformed_source;
            *parity_out = parity_after;
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_localdiff_parameter_renamed");
            }
            return true;
        }
        if (input_bytes != nullptr) {
            std::string restored_source = transformed_source;
            if (refii::lua::decompiler::TryApplyBytecodeBackedMainChunkRestorations(
                    *input_bytes,
                    std::string(chunk_name),
                    &restored_source,
                    nullptr,
                    std::string_view())) {
                if (ValidateLuaSource(restored_source, chunk_name).empty()) {
                    refii::lua::audit::LuaBytecodeParityResult restored_parity;
                    if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                            parity_reference,
                            restored_source,
                            chunk_name,
                            &restored_parity) &&
                        restored_parity.exact_bytecode) {
                        *source = std::move(restored_source);
                        *parity_out = std::move(restored_parity);
                        if (note != nullptr && !note_prefix.empty()) {
                            *note = JoinNotes(
                                *note,
                                std::string(note_prefix) +
                                    "_localdiff_parameter_renamed");
                        }
                        return true;
                    }
                }
            }
        }

        // Header-only rename can still fail when the function body keeps using
        // the old argN token. Apply the same rename within this function span
        // (skipping nested functions) and keep it only if parity goes exact.
        std::unordered_map<std::string, std::string> replacements = {
            {candidate_name, expected_name}};
        const std::size_t function_end =
            refii::lua::FindRenderedFunctionBlockEnd(lines, line_index);
        if (function_end >= lines.size() || function_end <= line_index) {
            continue;
        }
        std::vector<std::string> scoped_lines = lines;
        bool blocked = false;
        for (std::size_t scoped_line = line_index; scoped_line <= function_end;
             ++scoped_line) {
            if (scoped_line != line_index &&
                refii::lua::IsRenderedFunctionStartLine(scoped_lines[scoped_line])) {
                const std::size_t nested_end =
                    refii::lua::FindRenderedFunctionBlockEnd(scoped_lines, scoped_line);
                if (nested_end >= scoped_lines.size() || nested_end <= scoped_line ||
                    nested_end > function_end) {
                    blocked = true;
                    break;
                }
                scoped_line = nested_end;
                continue;
            }
            scoped_lines[scoped_line] =
                RewriteLuaIdentifiersWithMap(scoped_lines[scoped_line], replacements);
        }
        if (blocked) {
            continue;
        }

        const std::string scoped_source = JoinLines(scoped_lines, had_trailing_newline);
        if (!ValidateLuaSource(scoped_source, chunk_name).empty()) {
            continue;
        }

        refii::lua::audit::LuaBytecodeParityResult scoped_parity;
        if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(parity_reference,
                                                               scoped_source,
                                                               chunk_name,
                                                               &scoped_parity) &&
            scoped_parity.exact_bytecode) {
            *source = scoped_source;
            *parity_out = scoped_parity;
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_localdiff_parameter_renamed_scoped");
            }
            return true;
        }
        if (input_bytes != nullptr) {
            std::string restored_scoped_source = scoped_source;
            if (refii::lua::decompiler::TryApplyBytecodeBackedMainChunkRestorations(
                    *input_bytes,
                    std::string(chunk_name),
                    &restored_scoped_source,
                    nullptr,
                    std::string_view()) &&
                ValidateLuaSource(restored_scoped_source, chunk_name).empty()) {
                refii::lua::audit::LuaBytecodeParityResult restored_scoped_parity;
                if (refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                        parity_reference,
                        restored_scoped_source,
                        chunk_name,
                        &restored_scoped_parity) &&
                    restored_scoped_parity.exact_bytecode) {
                    *source = std::move(restored_scoped_source);
                    *parity_out = std::move(restored_scoped_parity);
                    if (note != nullptr && !note_prefix.empty()) {
                        *note = JoinNotes(
                            *note,
                            std::string(note_prefix) +
                                "_localdiff_parameter_renamed_scoped");
                    }
                    return true;
                }
            }
        }
    }

    return false;
}

}  // namespace refii::lua::internal
