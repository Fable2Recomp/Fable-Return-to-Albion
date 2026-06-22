#include "tools/decompiler/rendered/exact_bytecode_parity_repair_pipeline.h"

#include "tools/decompiler/rendered/bytecode_parity_repair_context.h"
#include "tools/decompiler/rendered/bytecode_parity_repair_parsing.h"
#include "tools/decompiler/rendered/exact_instruction_microdiff.h"
#include "tools/decompiler/rendered/exact_seed_normalization.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/bytecode/lua_bytecode_oracles.h"
#include "tools/decompiler/repair/lua_decompile_repair_context.h"
#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/decompiler/repair/lua_decompiled_function_spans.h"
#include "tools/decompiler/repair/lua_function_suffix_and_groupmember_repairs.h"
#include "tools/decompiler/repair/lua_reference_exact_repairs.h"
#include "tools/decompiler/repair/lua_tiny_main_repairs.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace refii::lua::internal {

namespace {

thread_local const ExactBytecodeParityRepairPipelineCallbacks* g_active_callbacks = nullptr;

bool IsIdentifierChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_';
}

template <typename Callback>
void ForEachLuaToken(std::string_view line, Callback&& callback) {
    const LuaTokenScanOptions scan_options{
        .mode = LuaTokenScanMode::kCommentAware,
        .parse_quoted_literals = true,
        .use_stable_copy = false,
    };
    ForEachLuaIdentifierToken(line, scan_options, callback);
}

TinyMainRepairOptions MakePipelineTinyMainRepairOptions() {
    TinyMainRepairOptions options;
    if (g_active_callbacks != nullptr) {
        options.normalize_create_subclass_seeds =
            g_active_callbacks->normalize_create_subclass_seeds;
        options.normalize_singleton_index_table_assignments =
            g_active_callbacks->normalize_singleton_index_table_assignments;
    }
    return options;
}

bool ApplyPipelineInstructionCleanupBundle(
    std::vector<std::string>* lines,
    bool include_local_timing,
    bool include_tiny_microdiff,
    bool include_polarity_cleanup) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->apply_instruction_cleanup_bundle != nullptr &&
           g_active_callbacks->apply_instruction_cleanup_bundle(lines,
                                                               include_local_timing,
                                                               include_tiny_microdiff,
                                                               include_polarity_cleanup);
}

bool ApplyPipelineInstructionPolarityCleanup(
    std::vector<std::string>* lines) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->apply_instruction_polarity_cleanup != nullptr &&
           g_active_callbacks->apply_instruction_polarity_cleanup(lines);
}

bool TryPipelineRenderNativeMainChunkCandidate(
    const std::vector<char>& input_bytes,
    std::string* decompiled_source,
    std::string* note,
    bool simplify_rendered_output,
    std::string_view note_prefix,
    std::string_view source_name_hint) {
    return g_active_callbacks != nullptr &&
           g_active_callbacks->try_render_native_main_chunk_candidate != nullptr &&
           g_active_callbacks->try_render_native_main_chunk_candidate(input_bytes,
                                                                     decompiled_source,
                                                                     note,
                                                                     simplify_rendered_output,
                                                                     note_prefix,
                                                                     source_name_hint);
}


bool TryRepairNearExactNegatedAndGuardToPositiveOrElse(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr || candidate_source->empty()) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string indent;
        std::string condition;
        if (!TryExtractBytecodeRepairIfCondition(lines[line_index], &indent, &condition)) {
            continue;
        }

        std::vector<std::string> and_parts;
        if (!SplitBytecodeRepairTopLevelBooleanParts(condition, "and", &and_parts) ||
            and_parts.size() < 4) {
            continue;
        }

        std::vector<std::string> positive_tail_parts;
        positive_tail_parts.reserve(and_parts.size() - 1);
        bool saw_negated_tail = false;
        bool saw_positive_tail = false;
        bool valid_shape = true;
        for (std::size_t part_index = 1; part_index < and_parts.size(); ++part_index) {
            std::string_view part = TrimWhitespace(and_parts[part_index]);
            if (part.starts_with("not ")) {
                std::string operand(TrimWhitespace(part.substr(4)));
                if (operand.empty()) {
                    valid_shape = false;
                    break;
                }
                positive_tail_parts.push_back(std::move(operand));
                saw_negated_tail = true;
                continue;
            }
            if (saw_positive_tail) {
                valid_shape = false;
                break;
            }
            positive_tail_parts.push_back(std::string(part));
            saw_positive_tail = true;
        }
        if (!valid_shape || !saw_negated_tail || !saw_positive_tail ||
            positive_tail_parts.size() < 2) {
            continue;
        }

        std::string rebuilt_tail;
        for (const std::string& part : positive_tail_parts) {
            if (rebuilt_tail.empty()) {
                rebuilt_tail = part;
            } else {
                rebuilt_tail += " or ";
                rebuilt_tail += part;
            }
        }
        if (rebuilt_tail.empty()) {
            continue;
        }

        std::vector<std::string> trial_lines = lines;
        trial_lines[line_index] = indent + "if " + and_parts.front() + " and (" +
                                  rebuilt_tail + ") then";
        if (TryCommitBytecodeParityImprovingLines(
                context,
                trial_lines,
                had_trailing_newline,
                "bytecode_near_exact_negated_and_guard_to_positive_or_else",
                candidate_source)) {
            lines = std::move(trial_lines);
            changed = true;
        }
    }

    return changed;
}

bool TryRepairNearExactFunctionTimingCleanup(const BytecodeParityRepairContext& context,
                                             std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }

    const bool tiny_instruction_only =
        std::llabs(context.mismatch.instruction_delta) <= 1 &&
        std::llabs(context.mismatch.constant_delta) == 0 &&
        std::llabs(context.mismatch.local_delta) == 0;
    const bool tiny_local_only =
        std::llabs(context.mismatch.instruction_delta) <= 1 &&
        std::llabs(context.mismatch.constant_delta) == 0 &&
        std::llabs(context.mismatch.local_delta) <= 2;
    if (!tiny_instruction_only && !tiny_local_only) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const bool changed = ApplyPipelineInstructionCleanupBundle(
        &lines,
        true,
        tiny_instruction_only || tiny_local_only,
        tiny_instruction_only || tiny_local_only);
    if (!changed) {
        return false;
    }

    const auto try_commit_transformed_lines = [&](const std::vector<std::string>& candidate_lines,
                                                  std::string_view chunk_name) {
        const std::string transformed_source =
            JoinLines(candidate_lines, had_trailing_newline);
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(transformed_source,
                                           std::string(chunk_name).c_str(),
                                           &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            return false;
        }

        if (context.mismatch.function_index == 0) {
            // Keep near-exact timing cleanup strict for main chunk: this lane should
            // only claim success when it reaches exact parity for main, otherwise the
            // follow-up tiny-main reference repair should continue.
            if (!Lua51FunctionBytecodeExact(context.original_chunk.main,
                                            transformed_chunk.chunk.main)) {
                return false;
            }
        } else if (!Lua51FunctionBytecodeExactInChunk(context.original_chunk,
                                                      transformed_chunk.chunk,
                                                      context.mismatch.function_index)) {
            return false;
        }

        *candidate_source = transformed_source;
        return true;
    };

    if (try_commit_transformed_lines(lines, "bytecode_parity_candidate_near_exact_timing")) {
        return true;
    }

    if (!tiny_instruction_only && !tiny_local_only) {
        return false;
    }

    std::vector<std::string> polarity_lines = lines;
    if (ApplyPipelineInstructionPolarityCleanup(&polarity_lines) &&
        try_commit_transformed_lines(polarity_lines,
                                     "bytecode_parity_candidate_near_exact_polarity")) {
        return true;
    }

    std::vector<std::string> polarity_from_original = SplitLines(*candidate_source,
                                                                 &had_trailing_newline);
    if (ApplyPipelineInstructionPolarityCleanup(&polarity_from_original) &&
        try_commit_transformed_lines(polarity_from_original,
                                     "bytecode_parity_candidate_near_exact_polarity_raw")) {
        return true;
    }

    return false;
}

bool TryRepairNearExactMissingElseBranchJump(const BytecodeParityRepairContext& context,
                                             std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) != 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto find_block_end = [&](std::size_t start_line) {
        int end_balance = 0;
        int until_balance = 0;
        for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
            const std::string_view stripped = StripLineComment(lines[line_index]);
            ForEachLuaToken(stripped, [&](std::string_view token, std::size_t, std::size_t) {
                if (token == "repeat") {
                    ++until_balance;
                    return;
                }
                if (token == "until") {
                    until_balance = std::max(0, until_balance - 1);
                    return;
                }
                if (token == "function" || token == "if" || token == "for" ||
                    token == "while" || token == "do") {
                    ++end_balance;
                    return;
                }
                if (token == "end") {
                    end_balance = std::max(0, end_balance - 1);
                }
            });

            if (line_index > start_line && end_balance == 0 && until_balance == 0) {
                return line_index;
            }
        }
        return lines.size();
    };
    const auto extract_leading_indent = [](std::string_view line) {
        std::size_t indent_end = 0;
        while (indent_end < line.size() &&
               (line[indent_end] == ' ' || line[indent_end] == '\t')) {
            ++indent_end;
        }
        return std::string(line.substr(0, indent_end));
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view if_trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::size_t inner_end_line = find_block_end(line_index);
        if (inner_end_line == lines.size()) {
            continue;
        }

        const std::size_t branch_line = next_nonempty_line(inner_end_line + 1);
        const std::size_t outer_end_line = next_nonempty_line(branch_line + 1);
        if (branch_line >= lines.size() || outer_end_line >= lines.size()) {
            continue;
        }

        const std::string branch_trimmed =
            std::string(TrimWhitespace(StripLineComment(lines[branch_line])));
        const std::string_view outer_end_trimmed =
            TrimWhitespace(StripLineComment(lines[outer_end_line]));
        if (branch_trimmed.empty() ||
            branch_trimmed == "end" ||
            branch_trimmed == "else" ||
            StartsWith(branch_trimmed, "elseif ") ||
            StartsWith(branch_trimmed, "if ") ||
            StartsWith(branch_trimmed, "function") ||
            outer_end_trimmed != "end") {
            continue;
        }

        const std::string if_indent = extract_leading_indent(lines[line_index]);
        std::vector<std::string> transformed_lines = lines;
        transformed_lines[inner_end_line] = if_indent + "else";
        transformed_lines[branch_line] = if_indent + "  " + branch_trimmed;

        const std::string transformed_source =
            JoinLines(transformed_lines, had_trailing_newline);
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(transformed_source,
                                           "bytecode_parity_candidate_missing_else_jump",
                                           &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            continue;
        }

        const bool target_exact =
            context.mismatch.function_index == 0
                ? Lua51FunctionBytecodeExact(context.original_chunk.main,
                                             transformed_chunk.chunk.main)
                : Lua51FunctionBytecodeExactInChunk(context.original_chunk,
                                                    transformed_chunk.chunk,
                                                    context.mismatch.function_index);
        if (!target_exact) {
            continue;
        }

        *candidate_source = transformed_source;
        return true;
    }

    return false;
}

bool TryRepairNearExactNavigationParamsMaxSpeedClamp(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr ||
        (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
         context.mismatch.note.find("instruction_diff") == std::string::npos)) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 9) {
        return false;
    }

    const auto line_trimmed = [&](std::size_t line_index) {
        return line_index < lines.size()
                   ? std::string(TrimWhitespace(StripLineComment(lines[line_index])))
                   : std::string();
    };
    const auto replace_identifier =
        [](std::string_view line,
           std::string_view identifier,
           std::string_view replacement,
           std::string* output) {
            if (output == nullptr || identifier.empty()) {
                return false;
            }

            std::string transformed;
            transformed.reserve(line.size() + replacement.size());
            bool changed = false;
            char quote = '\0';
            bool escaped = false;
            for (std::size_t index = 0; index < line.size();) {
                const char ch = line[index];
                if (quote == '\0' && ch == '-' && index + 1 < line.size() &&
                    line[index + 1] == '-') {
                    transformed.append(line.substr(index));
                    break;
                }
                if (quote != '\0') {
                    transformed.push_back(ch);
                    if (escaped) {
                        escaped = false;
                    } else if (ch == '\\') {
                        escaped = true;
                    } else if (ch == quote) {
                        quote = '\0';
                    }
                    ++index;
                    continue;
                }
                if (ch == '\'' || ch == '"') {
                    quote = ch;
                    transformed.push_back(ch);
                    ++index;
                    continue;
                }
                if (index + identifier.size() <= line.size() &&
                    line.substr(index, identifier.size()) == identifier) {
                    const bool left_ok =
                        index == 0 || !IsIdentifierChar(line[index - 1]);
                    const std::size_t right = index + identifier.size();
                    const bool right_ok =
                        right >= line.size() || !IsIdentifierChar(line[right]);
                    if (left_ok && right_ok) {
                        transformed.append(replacement);
                        index += identifier.size();
                        changed = true;
                        continue;
                    }
                }
                transformed.push_back(ch);
                ++index;
            }
            if (!changed || quote != '\0') {
                return false;
            }
            *output = std::move(transformed);
            return true;
        };

    const std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    for (std::size_t local_line = 0; local_line + 8 < lines.size(); ++local_line) {
        std::string table_name;
        std::string initializer_rhs;
        if (!ParseBytecodeRepairLocalInitializer(lines[local_line],
                                                 &table_name,
                                                 &initializer_rhs) ||
            !refii::lua::IsValidLuaIdentifier(table_name) ||
            initializer_rhs.find("speed = ENavigationSpeed.NAV_SPEED_RUN") ==
                std::string::npos ||
            initializer_rhs.find("reset_constraints = false") == std::string::npos) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[local_line]);
        if (GetLuaLineIndent(lines[local_line + 1]) != indent ||
            GetLuaLineIndent(lines[local_line + 2]) != indent ||
            GetLuaLineIndent(lines[local_line + 3]) != indent ||
            GetLuaLineIndent(lines[local_line + 4]) != indent ||
            GetLuaLineIndent(lines[local_line + 5]) != indent ||
            GetLuaLineIndent(lines[local_line + 6]) != indent ||
            GetLuaLineIndent(lines[local_line + 7]) != indent ||
            GetLuaLineIndent(lines[local_line + 8]) != indent) {
            continue;
        }

        if (line_trimmed(local_line + 1) !=
                "if not ModeManager.HasMode(self.Entity, EEntityModeType.EM_FLYING) then" ||
            line_trimmed(local_line + 2).find(table_name + ".speed = ") != 0 ||
            line_trimmed(local_line + 2).find("ENavigationSpeed.") != std::string::npos ||
            line_trimmed(local_line + 3) != "end" ||
            line_trimmed(local_line + 4) != "if self.CombatInfo.Style.MaxNavSpeed then" ||
            line_trimmed(local_line + 5) !=
                "if self.CombatInfo.Style.MaxNavSpeed < " + table_name + ".speed then" ||
            line_trimmed(local_line + 6) !=
                table_name + " = {speed = self.CombatInfo.Style.MaxNavSpeed}" ||
            line_trimmed(local_line + 7) != "end" ||
            line_trimmed(local_line + 8) != "end") {
            continue;
        }

        std::size_t function_end_line = lines.size();
        for (const DecompiledFunctionSpan& span : spans) {
            if (span.start_line <= local_line && local_line <= span.end_line) {
                function_end_line = span.end_line;
                break;
            }
        }
        if (function_end_line == lines.size() || function_end_line <= local_line) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[local_line + 2] =
            indent + table_name + ".speed = ENavigationSpeed.NAV_SPEED_WALK";
        transformed_lines[local_line + 5] =
            indent + "if " + table_name +
            ".speed > self.CombatInfo.Style.MaxNavSpeed then";
        transformed_lines[local_line + 6] =
            indent + table_name + ".speed = self.CombatInfo.Style.MaxNavSpeed";

        if (table_name != "params") {
            for (std::size_t line_index = local_line;
                 line_index <= function_end_line && line_index < transformed_lines.size();
                 ++line_index) {
                std::string replaced;
                if (replace_identifier(transformed_lines[line_index],
                                       table_name,
                                       "params",
                                       &replaced)) {
                    transformed_lines[line_index] = std::move(replaced);
                }
            }
        }

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_near_exact_navigation_params_max_speed_clamp",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactGuardedMessageElseQuestPrimaryBundle(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr ||
        context.mismatch.note.find("JMP") == std::string::npos ||
        context.mismatch.instruction_delta != 0 ||
        context.mismatch.constant_delta != 0 ||
        context.mismatch.local_delta != 0) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const auto line_trimmed = [&](std::size_t line_index) {
        return line_index < lines.size()
                   ? std::string(TrimWhitespace(StripLineComment(lines[line_index])))
                   : std::string();
    };
    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!line_trimmed(i).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    std::size_t guard_end_line = lines.size();
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string guard = line_trimmed(line_index);
        if (!StartsWith(guard, "if not ") ||
            guard.find("CheckIfValidPlaceToUseItem") == std::string::npos ||
            !guard.ends_with(" then")) {
            continue;
        }

        const std::size_t message_line = next_nonempty_line(line_index + 1);
        const std::size_t end_line = next_nonempty_line(message_line + 1);
        if (message_line >= lines.size() || end_line >= lines.size() ||
            line_trimmed(message_line).find("GUI.DisplayMessageBox(") ==
                std::string::npos ||
            line_trimmed(end_line) != "end") {
            continue;
        }
        guard_end_line = end_line;
        break;
    }
    if (guard_end_line >= lines.size()) {
        return false;
    }

    bool localized_followers = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string stripped = line_trimmed(line_index);
        constexpr std::string_view kForPrefix = "for ";
        constexpr std::string_view kInIpairs = " in ipairs(";
        if (!StartsWith(stripped, kForPrefix) || !stripped.ends_with(") do")) {
            continue;
        }
        const std::size_t in_pos = stripped.find(kInIpairs);
        if (in_pos == std::string::npos ||
            stripped.find("Follow.GetListOfFollowers(",
                          in_pos + kInIpairs.size()) == std::string::npos) {
            continue;
        }
        const std::size_t call_start = in_pos + kInIpairs.size();
        const std::size_t call_end = stripped.size() - std::string_view(") do").size();
        if (call_end <= call_start) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        const std::string for_head = stripped.substr(0, in_pos);
        const std::string call_expression = stripped.substr(call_start,
                                                            call_end - call_start);
        lines[line_index] = indent + "local all_followers = " + call_expression;
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     indent + for_head + " in ipairs(all_followers) do");
        localized_followers = true;
        break;
    }
    if (!localized_followers || guard_end_line >= lines.size()) {
        return false;
    }

    bool collapsed_primary_seed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines.size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLine(lines[line_index],
                                         &is_local,
                                         &lhs_names,
                                         &rhs_expression) ||
            !is_local ||
            lhs_names.size() != 1 ||
            rhs_expression.find("QuestTracker.IsPrimary(") == std::string::npos) {
            continue;
        }

        const std::string temp_name = std::string(TrimWhitespace(lhs_names.front()));
        if (!IsRenderTemporaryName(temp_name) &&
            !StartsWith(temp_name, "refii_seeded_call_arg_")) {
            continue;
        }

        const std::string if_line = line_trimmed(line_index + 1);
        const std::string seed_line = line_trimmed(line_index + 2);
        const std::string seed_end_line = line_trimmed(line_index + 3);
        const std::string set_primary_line = line_trimmed(line_index + 4);
        if (if_line != "if not " + temp_name + " then" ||
            seed_line.find(temp_name + " = ") != 0 ||
            seed_end_line != "end" ||
            set_primary_line.find("QuestTracker.SetAsPrimary(") != 0) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        std::vector<std::string> replacement;
        replacement.push_back(indent + "if not " +
                              StripBytecodeRepairWholeParens(
                                  TrimWhitespace(rhs_expression)) +
                              " then");
        replacement.push_back(indent + set_primary_line);
        replacement.push_back(indent + "end");
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(line_index + 5));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        collapsed_primary_seed = true;
        break;
    }
    if (!collapsed_primary_seed || guard_end_line >= lines.size()) {
        return false;
    }

    std::size_t function_end_line = lines.size();
    const std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    for (const DecompiledFunctionSpan& span : spans) {
        if (span.start_line <= guard_end_line && guard_end_line <= span.end_line) {
            function_end_line = span.end_line;
            break;
        }
    }
    if (function_end_line == lines.size() || function_end_line == 0) {
        return false;
    }

    std::size_t outer_end_line = lines.size();
    for (std::size_t line_index = function_end_line; line_index-- > 0;) {
        if (line_trimmed(line_index) == "end") {
            outer_end_line = line_index;
            break;
        }
    }
    if (outer_end_line == lines.size() || outer_end_line <= guard_end_line) {
        return false;
    }

    const std::string guard_indent = GetLuaLineIndent(lines[guard_end_line]);
    lines[guard_end_line] = guard_indent + "else";
    lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(outer_end_line),
                 guard_indent + "end");

    if (TryCommitBytecodeParityExactCurrentFunctionLines(
            context,
            lines,
            had_trailing_newline,
            "bytecode_near_exact_guarded_message_else_quest_primary_bundle_exact",
            candidate_source) ||
        TryCommitBytecodeParityImprovingLines(
            context,
            lines,
            had_trailing_newline,
            "bytecode_near_exact_guarded_message_else_quest_primary_bundle",
            candidate_source)) {
        return true;
    }

    return false;
}

bool TryRepairNearExactEmptyGuardReturnShortCircuit(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }
    if (lines.size() < 3) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t if_line = 0; if_line < lines.size(); ++if_line) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment(lines[if_line]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::string condition =
            StripBytecodeRepairWholeParens(
                if_trimmed.substr(3, if_trimmed.size() - 3 - 5));
        if (condition.empty()) {
            continue;
        }

        const std::size_t end_line = next_nonempty_line(if_line + 1);
        const std::size_t return_line = next_nonempty_line(end_line + 1);
        if (end_line >= lines.size() ||
            return_line >= lines.size() ||
            TrimWhitespace(StripLineComment(lines[end_line])) != "end") {
            continue;
        }

        const std::string_view return_trimmed =
            TrimWhitespace(StripLineComment(lines[return_line]));
        if (!StartsWith(return_trimmed, "return ")) {
            continue;
        }

        const std::string return_expression =
            StripBytecodeRepairWholeParens(return_trimmed.substr(7));
        if (return_expression.empty() ||
            return_expression.find(',') != std::string::npos) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[if_line] =
            std::string(GetLuaLineIndent(lines[if_line])) + "return " +
            condition + " and " + return_expression;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(if_line + 1),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(return_line + 1));

        const auto try_commit_short_circuit =
            [&](const std::vector<std::string>& candidate_lines,
                std::string_view label) {
                return TryCommitBytecodeParityImprovingLines(
                           context,
                           candidate_lines,
                           had_trailing_newline,
                           label,
                           candidate_source) ||
                       TryCommitBytecodeParityExactCurrentFunctionLines(
                           context,
                           candidate_lines,
                           had_trailing_newline,
                           label,
                           candidate_source);
            };
        if (try_commit_short_circuit(
                transformed_lines,
                "bytecode_parity_candidate_empty_guard_return_short_circuit")) {
            return true;
        }

        transformed_lines[if_line] =
            std::string(GetLuaLineIndent(lines[if_line])) + "return (" +
            condition + ") and " + return_expression;
        if (try_commit_short_circuit(
                transformed_lines,
                "bytecode_parity_candidate_empty_guard_return_short_circuit_grouped")) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactGuardedReturnFalseShortCircuit(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 3 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t if_line = 0; if_line < lines.size(); ++if_line) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment(lines[if_line]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::size_t inner_return_line = next_nonempty_line(if_line + 1);
        const std::size_t end_line = next_nonempty_line(inner_return_line + 1);
        const std::size_t false_return_line = next_nonempty_line(end_line + 1);
        if (inner_return_line >= lines.size() ||
            end_line >= lines.size() ||
            false_return_line >= lines.size() ||
            TrimWhitespace(StripLineComment(lines[end_line])) != "end" ||
            TrimWhitespace(StripLineComment(lines[false_return_line])) != "return false") {
            continue;
        }

        const std::string_view inner_return_trimmed =
            TrimWhitespace(StripLineComment(lines[inner_return_line]));
        if (!StartsWith(inner_return_trimmed, "return ")) {
            continue;
        }

        const std::string condition =
            StripBytecodeRepairWholeParens(
                if_trimmed.substr(3, if_trimmed.size() - 3 - 5));
        const std::string return_expression =
            StripBytecodeRepairWholeParens(inner_return_trimmed.substr(7));
        if (condition.empty() ||
            return_expression.empty() ||
            return_expression.find(',') != std::string::npos) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[if_line] =
            std::string(GetLuaLineIndent(lines[if_line])) + "return " +
            condition + " and " + return_expression;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(if_line + 1),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(false_return_line + 1));

        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_guarded_return_false_short_circuit",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_guarded_return_false_short_circuit_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactNestedGuardBooleanReturnExpression(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 6) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t if_line = 0; if_line < lines.size(); ++if_line) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment(lines[if_line]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        std::string first_positive;
        std::string second_positive;
        std::string carry_condition;
        if (!ParseBytecodeRepairNotOrNotAndCondition(
                if_trimmed.substr(3, if_trimmed.size() - 3 - 5),
                &first_positive,
                &second_positive,
                &carry_condition)) {
            continue;
        }

        const std::size_t nested_if_line = next_nonempty_line(if_line + 1);
        const std::size_t nested_end_line = next_nonempty_line(nested_if_line + 1);
        const std::size_t else_line = next_nonempty_line(nested_end_line + 1);
        const std::size_t outer_end_line = next_nonempty_line(else_line + 1);
        const std::size_t return_line = next_nonempty_line(outer_end_line + 1);
        if (nested_if_line >= lines.size() ||
            nested_end_line >= lines.size() ||
            else_line >= lines.size() ||
            outer_end_line >= lines.size() ||
            return_line >= lines.size()) {
            continue;
        }

        const std::string_view nested_if_trimmed =
            TrimWhitespace(StripLineComment(lines[nested_if_line]));
        if (!StartsWith(nested_if_trimmed, "if ") ||
            !nested_if_trimmed.ends_with(" then") ||
            TrimWhitespace(StripLineComment(lines[nested_end_line])) != "end" ||
            TrimWhitespace(StripLineComment(lines[else_line])) != "else" ||
            TrimWhitespace(StripLineComment(lines[outer_end_line])) != "end") {
            continue;
        }

        const std::string nested_condition =
            StripBytecodeRepairWholeParens(
                nested_if_trimmed.substr(3, nested_if_trimmed.size() - 3 - 5));
        const std::string_view return_trimmed =
            TrimWhitespace(StripLineComment(lines[return_line]));
        if (nested_condition.empty() || !StartsWith(return_trimmed, "return ")) {
            continue;
        }

        const std::string return_expression =
            StripBytecodeRepairWholeParens(return_trimmed.substr(7));
        if (return_expression.empty() ||
            return_expression.find(',') != std::string::npos) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[if_line] =
            std::string(GetLuaLineIndent(lines[if_line])) + "return (" +
            first_positive + " and " + second_positive + " or " +
            carry_condition + " and " + nested_condition + ") and " +
            return_expression;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(if_line + 1),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(return_line + 1));

        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_nested_guard_boolean_return",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_nested_guard_boolean_return_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactReturnedLocalShadowAssignment(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[line_index],
                                                 &is_local,
                                                 &lhs,
                                                 &rhs) ||
            !is_local ||
            lhs.empty() ||
            lhs.find('.') != std::string::npos ||
            lhs.find('[') != std::string::npos ||
            !IsBytecodeRepairSafeDottedPrefix(lhs)) {
            continue;
        }

        const std::size_t return_line = next_nonempty_line(line_index + 1);
        if (return_line >= lines.size()) {
            continue;
        }
        const std::string_view return_trimmed =
            TrimWhitespace(StripLineComment(lines[return_line]));
        if (return_trimmed != ("return " + lhs)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            std::string(GetLuaLineIndent(lines[line_index])) + lhs + " = " + rhs;
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_local_shadow_assignment",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_local_shadow_assignment_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactIfReturnBooleanToReturnExpression(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    const auto maybe_swapped_relational =
        [](std::string_view expression, std::string* swapped_out) {
            if (swapped_out == nullptr) {
                return false;
            }
            const std::string stripped = StripBytecodeRepairWholeParens(expression);
            const std::array<std::pair<std::string_view, std::string_view>, 4> ops = {
                std::pair<std::string_view, std::string_view>{">=", "<="},
                std::pair<std::string_view, std::string_view>{"<=", ">="},
                std::pair<std::string_view, std::string_view>{">", "<"},
                std::pair<std::string_view, std::string_view>{"<", ">"},
            };
            for (const auto& entry : ops) {
                const std::string token = " " + std::string(entry.first) + " ";
                const std::size_t op_pos =
                    FindBytecodeRepairTopLevelToken(stripped, token);
                if (op_pos == std::string_view::npos) {
                    continue;
                }
                const std::string lhs =
                    StripBytecodeRepairWholeParens(stripped.substr(0, op_pos));
                const std::string rhs = StripBytecodeRepairWholeParens(
                    stripped.substr(op_pos + token.size()));
                if (lhs.empty() || rhs.empty() ||
                    lhs.find(',') != std::string::npos ||
                    rhs.find(',') != std::string::npos) {
                    return false;
                }
                *swapped_out =
                    rhs + " " + std::string(entry.second) + " " + lhs;
                return true;
            }
            return false;
        };

    for (std::size_t if_line = 0; if_line < lines.size(); ++if_line) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment(lines[if_line]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::size_t inner_return_line = next_nonempty_line(if_line + 1);
        const std::size_t end_line = next_nonempty_line(inner_return_line + 1);
        const std::size_t tail_return_line = next_nonempty_line(end_line + 1);
        if (inner_return_line >= lines.size() ||
            end_line >= lines.size() ||
            tail_return_line >= lines.size() ||
            TrimWhitespace(StripLineComment(lines[end_line])) != "end") {
            continue;
        }

        const std::string_view inner_return =
            TrimWhitespace(StripLineComment(lines[inner_return_line]));
        const std::string_view tail_return =
            TrimWhitespace(StripLineComment(lines[tail_return_line]));
        if (!((inner_return == "return true" && tail_return == "return false") ||
              (inner_return == "return false" && tail_return == "return true"))) {
            continue;
        }

        const std::string condition =
            StripBytecodeRepairWholeParens(
                if_trimmed.substr(3, if_trimmed.size() - 3 - 5));
        if (condition.empty() || condition.find(',') != std::string::npos) {
            continue;
        }

        std::vector<std::string> expressions;
        if (inner_return == "return true") {
            std::string swapped;
            if (maybe_swapped_relational(condition, &swapped)) {
                expressions.push_back(std::move(swapped));
            }
            expressions.push_back(condition);
        } else {
            expressions.push_back("not (" + condition + ")");
        }

        for (const std::string& expression : expressions) {
            std::vector<std::string> transformed_lines = lines;
            transformed_lines[if_line] =
                std::string(GetLuaLineIndent(lines[if_line])) + "return " +
                expression;
            transformed_lines.erase(
                transformed_lines.begin() +
                    static_cast<std::ptrdiff_t>(if_line + 1),
                transformed_lines.begin() +
                    static_cast<std::ptrdiff_t>(tail_return_line + 1));

            if (TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_if_return_boolean_to_return_expression_exact",
                    candidate_source)) {
                return true;
            }
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_if_return_boolean_to_return_expression",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactHoistPairsCallIteratorAndDropBreak(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("GETTABLE") == std::string::npos &&
        context.mismatch.note.find("GETGLOBAL") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 6) {
        return false;
    }

    for (std::size_t for_line = 0; for_line < lines.size(); ++for_line) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment(lines[for_line]));
        if (!StartsWith(trimmed, "for ") || !trimmed.ends_with(") do")) {
            continue;
        }

        const std::size_t in_pos =
            FindBytecodeRepairTopLevelToken(trimmed, " in pairs(");
        if (in_pos == std::string_view::npos) {
            continue;
        }
        const std::string vars =
            std::string(TrimWhitespace(trimmed.substr(4, in_pos - 4)));
        std::vector<std::string> loop_vars = SplitBytecodeRepairTopLevelArgs(vars);
        if (loop_vars.size() < 2) {
            continue;
        }
        for (std::string& loop_var : loop_vars) {
            loop_var = std::string(TrimWhitespace(loop_var));
        }
        if (!refii::lua::IsValidLuaIdentifier(loop_vars[1])) {
            continue;
        }

        const std::size_t call_start = in_pos + std::string_view(" in pairs(").size();
        const std::size_t call_end = trimmed.size() - std::string_view(") do").size();
        if (call_end <= call_start) {
            continue;
        }
        const std::string iterable_call =
            StripBytecodeRepairWholeParens(trimmed.substr(call_start,
                                                          call_end - call_start));
        if (!IsBytecodeRepairSimpleCallExpression(iterable_call)) {
            continue;
        }

        std::string iterable_local = loop_vars[1] + "s";
        if (!refii::lua::IsValidLuaIdentifier(iterable_local)) {
            continue;
        }

        const std::string indent(GetLuaLineIndent(lines[for_line]));
        std::vector<std::string> hoisted_lines = lines;
        hoisted_lines[for_line] =
            indent + "for " + vars + " in pairs(" + iterable_local + ") do";
        hoisted_lines.insert(hoisted_lines.begin() +
                                 static_cast<std::ptrdiff_t>(for_line),
                             indent + "local " + iterable_local + " = " +
                                 iterable_call);

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                hoisted_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_hoist_pairs_call_iterator_exact",
                candidate_source) ||
            TryCommitBytecodeParityImprovingLines(
                context,
                hoisted_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_hoist_pairs_call_iterator",
                candidate_source)) {
            return true;
        }

        const std::size_t scan_begin = for_line + 2;
        const std::size_t scan_end =
            std::min(hoisted_lines.size(), scan_begin + static_cast<std::size_t>(48));
        for (std::size_t break_line = scan_begin; break_line < scan_end; ++break_line) {
            const std::string_view break_trimmed =
                TrimWhitespace(StripLineComment(hoisted_lines[break_line]));
            if (StartsWith(break_trimmed, "function ")) {
                break;
            }
            if (break_trimmed != "break") {
                continue;
            }

            std::vector<std::string> transformed_lines = hoisted_lines;
            transformed_lines.erase(transformed_lines.begin() +
                                    static_cast<std::ptrdiff_t>(break_line));
            if (TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_hoist_pairs_call_iterator_drop_break_exact",
                    candidate_source)) {
                return true;
            }
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_hoist_pairs_call_iterator_drop_break",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactTableInsertInlineLiteralTemp(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("candidate_missing temp") == std::string::npos &&
        context.mismatch.note.find("NEWTABLE") == std::string::npos &&
        context.mismatch.note.find("SETLIST") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment(lines[line_index]));
        if (!StartsWith(trimmed, "table.insert(") || !trimmed.ends_with(")")) {
            continue;
        }

        const std::string_view args_body =
            trimmed.substr(std::string_view("table.insert(").size(),
                           trimmed.size() -
                               std::string_view("table.insert(").size() - 1);
        std::vector<std::string> args = SplitBytecodeRepairTopLevelArgs(args_body);
        if (args.size() != 2) {
            continue;
        }
        for (std::string& arg : args) {
            arg = std::string(TrimWhitespace(arg));
        }
        if (args[0].empty() || args[1].size() < 2 ||
            args[1].front() != '{' || args[1].back() != '}') {
            continue;
        }

        const std::string indent(GetLuaLineIndent(lines[line_index]));
        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            indent + "table.insert(" + args[0] + ", temp)";
        transformed_lines.insert(transformed_lines.begin() +
                                     static_cast<std::ptrdiff_t>(line_index),
                                 indent + "local temp = " + args[1]);

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_table_insert_inline_literal_temp_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_table_insert_inline_literal_temp",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactTopLevelDottedFieldTableSeedCluster(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr || context.mismatch.function_index != 0) {
        return false;
    }
    if (context.mismatch.note.find("NEWTABLE") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 12 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 3) {
        return false;
    }

    std::size_t first_function_line = lines.size();
    for (std::size_t index = 0; index < lines.size(); ++index) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment(lines[index]));
        if (StartsWith(trimmed, "function ") ||
            StartsWith(trimmed, "local function ") ||
            trimmed.find("= function(") != std::string_view::npos) {
            first_function_line = index;
            break;
        }
    }
    if (first_function_line < 2) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 1 < first_function_line;
         ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &lhs,
                                                &rhs) ||
            is_local ||
            TrimWhitespace(rhs) != "{}") {
            continue;
        }
        const std::size_t dot = lhs.find('.');
        if (dot == std::string::npos || dot == 0 || dot + 1 >= lhs.size()) {
            continue;
        }
        const std::string root = lhs.substr(0, dot);
        if (!refii::lua::IsValidLuaIdentifier(root)) {
            continue;
        }

        std::vector<std::string> fields;
        std::size_t scan = line_index;
        for (; scan < first_function_line; ++scan) {
            bool field_is_local = false;
            std::string field_lhs;
            std::string field_rhs;
            if (!ParseBytecodeRepairPlainAssignment(lines[scan],
                                                    &field_is_local,
                                                    &field_lhs,
                                                    &field_rhs) ||
                field_is_local ||
                TrimWhitespace(field_rhs) != "{}" ||
                !StartsWith(field_lhs, root + ".")) {
                break;
            }
            const std::string field = field_lhs.substr(root.size() + 1);
            if (!refii::lua::IsValidLuaIdentifier(field)) {
                break;
            }
            fields.push_back(field);
            if (fields.size() > 16) {
                break;
            }
        }
        if (fields.size() < 2) {
            continue;
        }

        std::string literal = root + " = {";
        for (std::size_t field_index = 0; field_index < fields.size();
             ++field_index) {
            if (field_index != 0) {
                literal += ", ";
            }
            literal += fields[field_index] + " = {}";
        }
        literal += "}";

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            std::string(GetLuaLineIndent(lines[line_index])) + literal;
        transformed_lines.erase(
            transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(line_index + 1),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(scan));

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_top_level_dotted_field_seed_cluster_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_top_level_dotted_field_seed_cluster",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactSplitLocalAddInitializer(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("local_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[line_index],
                                                 &is_local,
                                                 &lhs,
                                                 &rhs)) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[line_index]));
            constexpr std::string_view kLocalPrefix = "local ";
            const std::size_t equals_pos = trimmed.find(" = ");
            if (!StartsWith(trimmed, kLocalPrefix) ||
                equals_pos == std::string_view::npos ||
                equals_pos <= kLocalPrefix.size()) {
                continue;
            }
            is_local = true;
            lhs = std::string(TrimWhitespace(
                trimmed.substr(kLocalPrefix.size(),
                               equals_pos - kLocalPrefix.size())));
            rhs = std::string(TrimWhitespace(trimmed.substr(equals_pos + 3)));
        }

        if (!is_local ||
            lhs.empty() ||
            lhs.find('.') != std::string::npos ||
            lhs.find('[') != std::string::npos ||
            !IsBytecodeRepairSafeDottedPrefix(lhs)) {
            continue;
        }

        const std::string rhs_stripped = StripBytecodeRepairWholeParens(rhs);
        const std::size_t plus_pos =
            FindBytecodeRepairTopLevelToken(rhs_stripped, " + ");
        if (plus_pos == std::string::npos) {
            continue;
        }
        const std::string first_term =
            StripBytecodeRepairWholeParens(rhs_stripped.substr(0, plus_pos));
        const std::string second_term =
            StripBytecodeRepairWholeParens(rhs_stripped.substr(plus_pos + 3));
        if (first_term.empty() ||
            second_term.empty() ||
            refii::lua::CountIdentifierUses(second_term, lhs) != 0) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        const std::string indent(GetLuaLineIndent(lines[line_index]));
        transformed_lines[line_index] = indent + "local " + lhs + " = " + first_term;
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1),
            indent + lhs + " = " + lhs + " + " + second_term);
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_split_local_add_initializer",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_split_local_add_initializer_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactLocalCopyInitializerExpression(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        if (GetLuaLineIndent(lines[line_index]) !=
            GetLuaLineIndent(lines[line_index + 1])) {
            continue;
        }

        std::string source_name;
        std::string source_expression;
        std::string copy_name;
        std::string copy_expression;
        if (!ParseBytecodeRepairLocalInitializer(lines[line_index],
                                                 &source_name,
                                                 &source_expression) ||
            !ParseBytecodeRepairLocalInitializer(lines[line_index + 1],
                                                 &copy_name,
                                                 &copy_expression) ||
            source_name.empty() ||
            copy_name.empty() ||
            source_name == copy_name ||
            StripBytecodeRepairWholeParens(copy_expression) != source_name ||
            !IsBytecodeRepairSafeDottedPrefix(source_name) ||
            !IsBytecodeRepairSafeDottedPrefix(copy_name)) {
            continue;
        }

        const std::string stripped_source =
            StripBytecodeRepairWholeParens(source_expression);
        if (stripped_source.empty() ||
            refii::lua::CountIdentifierUses(stripped_source, copy_name) != 0 ||
            stripped_source.find("function") != std::string::npos ||
            stripped_source.find('{') != std::string::npos ||
            stripped_source.find('=') != std::string::npos) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index + 1] =
            std::string(GetLuaLineIndent(lines[line_index + 1])) + "local " +
            copy_name + " = " + source_expression;
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_local_copy_initializer_expression",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_local_copy_initializer_expression_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactFoldLocalSeedAddAssignment(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        if (GetLuaLineIndent(lines[line_index]) !=
            GetLuaLineIndent(lines[line_index + 1])) {
            continue;
        }

        std::string local_name;
        std::string seed_expression;
        if (!ParseBytecodeRepairLocalInitializer(lines[line_index],
                                                 &local_name,
                                                 &seed_expression) ||
            local_name.empty() ||
            !IsBytecodeRepairSafeDottedPrefix(local_name)) {
            continue;
        }

        bool is_local = false;
        std::string assignment_lhs;
        std::string assignment_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index + 1],
                                                 &is_local,
                                                 &assignment_lhs,
                                                 &assignment_rhs) ||
            is_local ||
            assignment_lhs != local_name) {
            continue;
        }

        const std::string rhs_stripped =
            StripBytecodeRepairWholeParens(assignment_rhs);
        constexpr std::string_view kPlus = " + ";
        if (!StartsWith(rhs_stripped, local_name) ||
            rhs_stripped.size() <= local_name.size() + kPlus.size() ||
            rhs_stripped.substr(local_name.size(), kPlus.size()) != kPlus) {
            continue;
        }

        const std::string delta_expression =
            StripBytecodeRepairWholeParens(
                rhs_stripped.substr(local_name.size() + kPlus.size()));
        if (delta_expression.empty() ||
            refii::lua::CountIdentifierUses(delta_expression, local_name) != 0) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            std::string(GetLuaLineIndent(lines[line_index])) + "local " +
            local_name + " = " + seed_expression + " + " + delta_expression;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_fold_local_seed_add_assignment",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_fold_local_seed_add_assignment_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactCopyInitializerAndFoldSeedAdd(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);

    for (std::size_t copy_line = 0; copy_line + 1 < lines.size(); ++copy_line) {
        if (GetLuaLineIndent(lines[copy_line]) !=
            GetLuaLineIndent(lines[copy_line + 1])) {
            continue;
        }

        std::string source_name;
        std::string source_expression;
        std::string copy_name;
        std::string copy_expression;
        if (!ParseBytecodeRepairLocalInitializer(lines[copy_line],
                                                 &source_name,
                                                 &source_expression) ||
            !ParseBytecodeRepairLocalInitializer(lines[copy_line + 1],
                                                 &copy_name,
                                                 &copy_expression) ||
            StripBytecodeRepairWholeParens(copy_expression) != source_name) {
            continue;
        }

        for (std::size_t fold_line = copy_line + 2;
             fold_line + 1 < lines.size() && fold_line < copy_line + 16;
             ++fold_line) {
            if (GetLuaLineIndent(lines[fold_line]) !=
                GetLuaLineIndent(lines[fold_line + 1])) {
                continue;
            }

            std::string local_name;
            std::string seed_expression;
            if (!ParseBytecodeRepairLocalInitializer(lines[fold_line],
                                                     &local_name,
                                                     &seed_expression) ||
                local_name.empty() ||
                !IsBytecodeRepairSafeDottedPrefix(local_name)) {
                continue;
            }

            bool assignment_is_local = false;
            std::string assignment_lhs;
            std::string assignment_rhs;
            if (!ParseBytecodeRepairPlainAssignment(lines[fold_line + 1],
                                                     &assignment_is_local,
                                                     &assignment_lhs,
                                                     &assignment_rhs) ||
                assignment_is_local ||
                assignment_lhs != local_name) {
                continue;
            }

            const std::string rhs_stripped =
                StripBytecodeRepairWholeParens(assignment_rhs);
            constexpr std::string_view kPlus = " + ";
            if (!StartsWith(rhs_stripped, local_name) ||
                rhs_stripped.size() <= local_name.size() + kPlus.size() ||
                rhs_stripped.substr(local_name.size(), kPlus.size()) != kPlus) {
                continue;
            }

            const std::string delta_expression =
                StripBytecodeRepairWholeParens(
                    rhs_stripped.substr(local_name.size() + kPlus.size()));
            if (delta_expression.empty() ||
                refii::lua::CountIdentifierUses(delta_expression, local_name) != 0) {
                continue;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines[copy_line + 1] =
                std::string(GetLuaLineIndent(lines[copy_line + 1])) + "local " +
                copy_name + " = " + source_expression;
            transformed_lines[fold_line] =
                std::string(GetLuaLineIndent(lines[fold_line])) + "local " +
                local_name + " = " + seed_expression + " + " + delta_expression;
            transformed_lines.erase(
                transformed_lines.begin() + static_cast<std::ptrdiff_t>(fold_line + 1));
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_copy_initializer_and_fold_seed_add",
                    candidate_source) ||
                TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_copy_initializer_and_fold_seed_add_exact",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactFoldLocalInitializerUseAssignment(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("local_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        if (GetLuaLineIndent(lines[line_index]) !=
            GetLuaLineIndent(lines[line_index + 1])) {
            continue;
        }

        std::string local_name;
        std::string initializer;
        if (!ParseBytecodeRepairLocalInitializer(lines[line_index],
                                                 &local_name,
                                                 &initializer) ||
            local_name.empty() ||
            !IsBytecodeRepairSafeDottedPrefix(local_name)) {
            continue;
        }

        bool assignment_is_local = false;
        std::string assignment_lhs;
        std::string assignment_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index + 1],
                                                 &assignment_is_local,
                                                 &assignment_lhs,
                                                 &assignment_rhs) ||
            assignment_is_local ||
            assignment_lhs != local_name) {
            continue;
        }

        const std::string rhs_stripped =
            StripBytecodeRepairWholeParens(assignment_rhs);
        std::string folded_expression;
        for (std::string_view op : {" + ", " * "}) {
            const std::size_t op_pos =
                FindBytecodeRepairTopLevelToken(rhs_stripped, op);
            if (op_pos == std::string::npos) {
                continue;
            }
            const std::string lhs =
                StripBytecodeRepairWholeParens(rhs_stripped.substr(0, op_pos));
            const std::string rhs =
                StripBytecodeRepairWholeParens(rhs_stripped.substr(op_pos + op.size()));
            if (lhs == local_name && !rhs.empty() &&
                refii::lua::CountIdentifierUses(rhs, local_name) == 0) {
                folded_expression = "(" + initializer + ")" + std::string(op) + rhs;
                break;
            }
            if (rhs == local_name && !lhs.empty() &&
                refii::lua::CountIdentifierUses(lhs, local_name) == 0) {
                folded_expression = lhs + std::string(op) + "(" + initializer + ")";
                break;
            }
        }
        if (folded_expression.empty()) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            std::string(GetLuaLineIndent(lines[line_index])) + "local " +
            local_name + " = " + folded_expression;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_fold_local_initializer_use_assignment",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_fold_local_initializer_use_assignment_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

struct BytecodeLocalInitializerUseFoldCandidate {
    std::size_t line = 0;
    std::string folded_line;
};

std::vector<BytecodeLocalInitializerUseFoldCandidate>
CollectBytecodeLocalInitializerUseFolds(const std::vector<std::string>& lines) {
    std::vector<BytecodeLocalInitializerUseFoldCandidate> folds;
    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        if (GetLuaLineIndent(lines[line_index]) !=
            GetLuaLineIndent(lines[line_index + 1])) {
            continue;
        }

        std::string local_name;
        std::string initializer;
        if (!ParseBytecodeRepairLocalInitializer(lines[line_index],
                                                 &local_name,
                                                 &initializer) ||
            local_name.empty() ||
            !IsBytecodeRepairSafeDottedPrefix(local_name)) {
            continue;
        }

        bool assignment_is_local = false;
        std::string assignment_lhs;
        std::string assignment_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index + 1],
                                                 &assignment_is_local,
                                                 &assignment_lhs,
                                                 &assignment_rhs) ||
            assignment_is_local ||
            assignment_lhs != local_name) {
            continue;
        }

        const std::string rhs_stripped =
            StripBytecodeRepairWholeParens(assignment_rhs);
        std::string folded_expression;
        for (std::string_view op : {" + ", " * "}) {
            const std::size_t op_pos =
                FindBytecodeRepairTopLevelToken(rhs_stripped, op);
            if (op_pos == std::string::npos) {
                continue;
            }
            const std::string lhs =
                StripBytecodeRepairWholeParens(rhs_stripped.substr(0, op_pos));
            const std::string rhs =
                StripBytecodeRepairWholeParens(rhs_stripped.substr(op_pos + op.size()));
            if (lhs == local_name && !rhs.empty() &&
                refii::lua::CountIdentifierUses(rhs, local_name) == 0) {
                folded_expression = "(" + initializer + ")" + std::string(op) + rhs;
                break;
            }
            if (rhs == local_name && !lhs.empty() &&
                refii::lua::CountIdentifierUses(lhs, local_name) == 0) {
                folded_expression = lhs + std::string(op) + "(" + initializer + ")";
                break;
            }
        }
        if (!folded_expression.empty()) {
            folds.push_back({line_index,
                             std::string(GetLuaLineIndent(lines[line_index])) +
                                 "local " + local_name + " = " + folded_expression});
        }
    }
    return folds;
}

bool TryExtractBytecodeLocalDiffName(std::string_view note, std::string* name_out) {
    if (name_out == nullptr) {
        return false;
    }
    name_out->clear();
    const std::size_t local_diff_pos = note.find("[local_diff");
    if (local_diff_pos == std::string_view::npos) {
        return false;
    }
    const std::size_t idx_pos = note.find("idx=", local_diff_pos);
    if (idx_pos == std::string_view::npos) {
        return false;
    }
    const std::size_t name_start = note.find(' ', idx_pos);
    if (name_start == std::string_view::npos || name_start + 1 >= note.size()) {
        return false;
    }
    const std::size_t at_pos = note.find('@', name_start + 1);
    if (at_pos == std::string_view::npos || at_pos <= name_start + 1) {
        return false;
    }
    const std::string name =
        std::string(TrimWhitespace(note.substr(name_start + 1,
                                               at_pos - name_start - 1)));
    if (!IsBytecodeRepairSafeDottedPrefix(name)) {
        return false;
    }
    *name_out = name;
    return true;
}

bool FoldCandidateDeclaresLocal(
    const std::vector<std::string>& lines,
    const BytecodeLocalInitializerUseFoldCandidate& fold,
    std::string_view local_name) {
    if (fold.line >= lines.size()) {
        return false;
    }
    std::string candidate_name;
    std::string initializer;
    return ParseBytecodeRepairLocalInitializer(lines[fold.line],
                                               &candidate_name,
                                               &initializer) &&
           candidate_name == local_name;
}

bool TryRepairNearExactFoldLocalDiffAnchoredInitializerUseAssignments(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr ||
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    std::string local_diff_name;
    if (!TryExtractBytecodeLocalDiffName(context.mismatch.note, &local_diff_name)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    const auto try_make_folded_line =
        [](const std::vector<std::string>& source_lines,
           std::size_t line_index,
           std::string* folded_line_out) {
            if (folded_line_out == nullptr ||
                line_index + 1 >= source_lines.size() ||
                GetLuaLineIndent(source_lines[line_index]) !=
                    GetLuaLineIndent(source_lines[line_index + 1])) {
                return false;
            }

            std::string local_name;
            std::string initializer;
            if (!ParseBytecodeRepairLocalInitializer(source_lines[line_index],
                                                     &local_name,
                                                     &initializer) ||
                local_name.empty() ||
                !IsBytecodeRepairSafeDottedPrefix(local_name)) {
                return false;
            }

            bool assignment_is_local = false;
            std::string assignment_lhs;
            std::string assignment_rhs;
            if (!ParseBytecodeRepairPlainAssignment(source_lines[line_index + 1],
                                                     &assignment_is_local,
                                                     &assignment_lhs,
                                                     &assignment_rhs) ||
                assignment_is_local ||
                assignment_lhs != local_name) {
                return false;
            }

            const std::string rhs =
                StripBytecodeRepairWholeParens(assignment_rhs);
            std::string folded_expression;
            for (std::string_view op : {" + ", " * "}) {
                const std::string prefix = local_name + std::string(op);
                if (StartsWith(rhs, prefix)) {
                    const std::string other =
                        StripBytecodeRepairWholeParens(rhs.substr(prefix.size()));
                    if (!other.empty() &&
                        refii::lua::CountIdentifierUses(other, local_name) == 0) {
                        folded_expression =
                            "(" + initializer + ")" + std::string(op) + other;
                        break;
                    }
                }

                const std::string suffix = std::string(op) + local_name;
                if (rhs.size() > suffix.size() &&
                    rhs.compare(rhs.size() - suffix.size(),
                                suffix.size(),
                                suffix) == 0) {
                    const std::string other = StripBytecodeRepairWholeParens(
                        std::string_view(rhs).substr(0, rhs.size() - suffix.size()));
                    if (!other.empty() &&
                        refii::lua::CountIdentifierUses(other, local_name) == 0) {
                        folded_expression =
                            other + std::string(op) + "(" + initializer + ")";
                        break;
                    }
                }
            }
            if (folded_expression.empty()) {
                return false;
            }

            *folded_line_out =
                std::string(GetLuaLineIndent(source_lines[line_index])) +
                "local " + local_name + " = " + folded_expression;
            return true;
        };

    for (std::size_t anchor_line = 0; anchor_line + 1 < lines.size(); ++anchor_line) {
        std::string anchor_name;
        std::string anchor_initializer;
        if (!ParseBytecodeRepairLocalInitializer(lines[anchor_line],
                                                 &anchor_name,
                                                 &anchor_initializer) ||
            anchor_name != local_diff_name) {
            continue;
        }

        std::string anchor_folded_line;
        if (!try_make_folded_line(lines, anchor_line, &anchor_folded_line)) {
            continue;
        }

        const std::size_t search_end =
            std::min(lines.size() - 1, anchor_line + 16);
        for (std::size_t other_line = anchor_line + 2;
             other_line < search_end;
             ++other_line) {
            std::string other_folded_line;
            if (!try_make_folded_line(lines, other_line, &other_folded_line)) {
                continue;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines[anchor_line] = anchor_folded_line;
            transformed_lines[other_line] = other_folded_line;
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(other_line + 1));
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(anchor_line + 1));
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_fold_local_diff_direct_initializer_use_assignments",
                    candidate_source) ||
                TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_fold_local_diff_direct_initializer_use_assignments_exact",
                    candidate_source)) {
                return true;
            }
        }
    }

    const std::vector<BytecodeLocalInitializerUseFoldCandidate> folds =
        CollectBytecodeLocalInitializerUseFolds(lines);
    if (folds.size() < 2) {
        return false;
    }

    for (std::size_t anchor = 0; anchor < folds.size(); ++anchor) {
        if (!FoldCandidateDeclaresLocal(lines, folds[anchor], local_diff_name)) {
            continue;
        }
        for (std::size_t other = 0; other < folds.size(); ++other) {
            if (other == anchor) {
                continue;
            }
            const std::size_t low = std::min(folds[anchor].line, folds[other].line);
            const std::size_t high = std::max(folds[anchor].line, folds[other].line);
            if (high <= low + 1 || high - low > 16) {
                continue;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines[folds[anchor].line] = folds[anchor].folded_line;
            transformed_lines[folds[other].line] = folds[other].folded_line;
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(high + 1));
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(low + 1));
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_fold_local_diff_anchored_initializer_use_assignments",
                    candidate_source) ||
                TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_fold_local_diff_anchored_initializer_use_assignments_exact",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactFoldPairedLocalInitializerUseAssignments(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("local_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);

    const std::vector<BytecodeLocalInitializerUseFoldCandidate> folds =
        CollectBytecodeLocalInitializerUseFolds(lines);

    for (std::size_t first = 0; first < folds.size(); ++first) {
        for (std::size_t second = first + 1; second < folds.size(); ++second) {
            if (folds[second].line <= folds[first].line + 1 ||
                folds[second].line > folds[first].line + 16) {
                continue;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines[folds[first].line] = folds[first].folded_line;
            transformed_lines[folds[second].line] = folds[second].folded_line;
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(folds[second].line + 1));
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(folds[first].line + 1));
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_fold_paired_local_initializer_use_assignments",
                    candidate_source) ||
                TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_fold_paired_local_initializer_use_assignments_exact",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactFoldBatchLocalInitializerUseAssignments(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("local_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    const std::vector<BytecodeLocalInitializerUseFoldCandidate> folds =
        CollectBytecodeLocalInitializerUseFolds(lines);
    if (folds.size() < 2 || folds.size() > 6) {
        return false;
    }
    if (folds.back().line <= folds.front().line ||
        folds.back().line - folds.front().line > 96) {
        return false;
    }

    std::vector<std::string> transformed_lines = lines;
    for (const BytecodeLocalInitializerUseFoldCandidate& fold : folds) {
        transformed_lines[fold.line] = fold.folded_line;
    }
    for (std::size_t index = folds.size(); index > 0; --index) {
        transformed_lines.erase(
            transformed_lines.begin() +
            static_cast<std::ptrdiff_t>(folds[index - 1].line + 1));
    }
    if (TryCommitBytecodeParityImprovingLines(
            context,
            transformed_lines,
            had_trailing_newline,
            "bytecode_parity_candidate_fold_batch_local_initializer_use_assignments",
            candidate_source) ||
        TryCommitBytecodeParityExactCurrentFunctionLines(
            context,
            transformed_lines,
            had_trailing_newline,
            "bytecode_parity_candidate_fold_batch_local_initializer_use_assignments_exact",
            candidate_source)) {
        return true;
    }

    return false;
}

bool TryRepairRepeatedScalarCallFromMultiResultSeed(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 8) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 3 || lines.size() > 12000) {
        return false;
    }

    const auto normalize_call = [](std::string_view expression) {
        std::string normalized;
        normalized.reserve(expression.size());
        for (const char ch : expression) {
            if (!std::isspace(static_cast<unsigned char>(ch))) {
                normalized.push_back(ch);
            }
        }
        return normalized;
    };
    const auto is_plain_call = [](std::string_view expression) {
        expression = TrimWhitespace(expression);
        if (expression.empty() ||
            expression.find(" and ") != std::string_view::npos ||
            expression.find(" or ") != std::string_view::npos ||
            expression.find('=') != std::string_view::npos ||
            expression.find('{') != std::string_view::npos ||
            expression.find('}') != std::string_view::npos) {
            return false;
        }
        const std::size_t open = expression.find('(');
        return open != std::string_view::npos &&
               expression.back() == ')' &&
               open > 0;
    };

    for (std::size_t seed_line = 0; seed_line + 1 < lines.size(); ++seed_line) {
        bool seed_is_local = false;
        std::vector<std::string> seed_lhs_names;
        std::string seed_rhs;
        if (!ParseBytecodeRepairAssignmentList(lines[seed_line],
                                               &seed_is_local,
                                               &seed_lhs_names,
                                               &seed_rhs) ||
            !seed_is_local ||
            seed_lhs_names.size() < 2 ||
            !is_plain_call(seed_rhs)) {
            continue;
        }

        const std::string first_result = seed_lhs_names.front();
        const std::string seed_call_key = normalize_call(seed_rhs);
        const std::string indent = std::string(GetLuaLineIndent(lines[seed_line]));
        const std::size_t search_end = std::min(lines.size(), seed_line + 24);
        bool first_result_overwritten = false;
        for (std::size_t assign_line = seed_line + 1;
             assign_line < search_end;
             ++assign_line) {
            if (GetLuaLineIndent(lines[assign_line]) != indent) {
                const std::string_view trimmed =
                    TrimWhitespace(StripLineComment(lines[assign_line]));
                if (StartsWith(trimmed, "function ") || StartsWith(trimmed, "end")) {
                    break;
                }
            }

            bool assign_is_local = false;
            std::vector<std::string> assign_lhs_names;
            std::string assign_rhs;
            if (!ParseBytecodeRepairAssignmentList(lines[assign_line],
                                                   &assign_is_local,
                                                   &assign_lhs_names,
                                                   &assign_rhs)) {
                continue;
            }
            if (assign_lhs_names.size() == 1 &&
                assign_lhs_names.front() == first_result) {
                first_result_overwritten = true;
                break;
            }
            if (assign_is_local ||
                assign_lhs_names.size() != 1 ||
                !is_plain_call(assign_rhs) ||
                normalize_call(assign_rhs) != seed_call_key) {
                continue;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines[assign_line] =
                std::string(GetLuaLineIndent(lines[assign_line])) +
                assign_lhs_names.front() + " = " + first_result;
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_repeated_scalar_call_from_multi_result_seed",
                    candidate_source) ||
                TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_repeated_scalar_call_from_multi_result_seed_exact",
                    candidate_source)) {
                return true;
            }
        }
        if (first_result_overwritten) {
            continue;
        }
    }

    return false;
}

bool TryRepairMultiResultTempFieldAssignments(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 12 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 12) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4 || lines.size() > 12000) {
        return false;
    }

    const auto is_plain_call = [](std::string_view expression) {
        expression = TrimWhitespace(expression);
        if (expression.empty() ||
            expression.find(" and ") != std::string_view::npos ||
            expression.find(" or ") != std::string_view::npos ||
            expression.find('=') != std::string_view::npos ||
            expression.find('{') != std::string_view::npos ||
            expression.find('}') != std::string_view::npos) {
            return false;
        }
        const std::size_t open = expression.find('(');
        return open != std::string_view::npos &&
               expression.back() == ')' &&
               open > 0;
    };

    for (std::size_t seed_line = 0; seed_line + 2 < lines.size(); ++seed_line) {
        bool seed_is_local = false;
        std::vector<std::string> seed_lhs_names;
        std::string seed_rhs;
        if (!ParseBytecodeRepairAssignmentList(lines[seed_line],
                                               &seed_is_local,
                                               &seed_lhs_names,
                                               &seed_rhs) ||
            !seed_is_local ||
            seed_lhs_names.size() < 3 ||
            seed_lhs_names.size() > 12 ||
            !is_plain_call(seed_rhs)) {
            continue;
        }

        const std::string indent = std::string(GetLuaLineIndent(lines[seed_line]));
        std::vector<std::string> replacement_targets(seed_lhs_names.size());
        std::vector<std::size_t> removable_lines;
        const std::size_t search_end = std::min(lines.size(), seed_line + 32);
        bool rejected = false;
        for (std::size_t scan_line = seed_line + 1; scan_line < search_end; ++scan_line) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[scan_line]));
            if (trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent(lines[scan_line]) != indent) {
                break;
            }

            bool assign_is_local = false;
            std::vector<std::string> assign_lhs_names;
            std::string assign_rhs;
            if (!ParseBytecodeRepairAssignmentList(lines[scan_line],
                                                   &assign_is_local,
                                                   &assign_lhs_names,
                                                   &assign_rhs) ||
                assign_is_local ||
                assign_lhs_names.size() != 1) {
                continue;
            }

            const std::string rhs = StripBytecodeRepairWholeParens(assign_rhs);
            auto seed_it =
                std::find(seed_lhs_names.begin(), seed_lhs_names.end(), rhs);
            if (seed_it == seed_lhs_names.end()) {
                for (const std::string& seed_name : seed_lhs_names) {
                    if (assign_lhs_names.front() == seed_name) {
                        rejected = true;
                        break;
                    }
                }
                if (rejected) {
                    break;
                }
                continue;
            }

            const std::size_t result_index =
                static_cast<std::size_t>(std::distance(seed_lhs_names.begin(), seed_it));
            if (!replacement_targets[result_index].empty()) {
                rejected = true;
                break;
            }
            replacement_targets[result_index] = assign_lhs_names.front();
            removable_lines.push_back(scan_line);
        }
        if (rejected || removable_lines.size() != seed_lhs_names.size()) {
            continue;
        }

        std::string lhs_joined;
        for (std::size_t index = 0; index < replacement_targets.size(); ++index) {
            if (replacement_targets[index].empty()) {
                rejected = true;
                break;
            }
            if (index != 0) {
                lhs_joined += ", ";
            }
            lhs_joined += replacement_targets[index];
        }
        if (rejected) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[seed_line] = indent + lhs_joined + " = " + seed_rhs;
        for (std::size_t index = removable_lines.size(); index > 0; --index) {
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(removable_lines[index - 1]));
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_multi_result_temp_field_assignments",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_multi_result_temp_field_assignments_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairMalformedDofAssemblyConditionExpressions(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 12 || lines.size() > 12000) {
        return false;
    }

    const auto line_contains = [](const std::vector<std::string>& haystack,
                                  std::size_t begin,
                                  std::size_t end,
                                  std::string_view needle) {
        for (std::size_t index = begin; index < end && index < haystack.size(); ++index) {
            if (haystack[index].find(needle) != std::string::npos) {
                return true;
            }
        }
        return false;
    };

    for (std::size_t function_line = 0; function_line < lines.size(); ++function_line) {
        const std::string_view function_trimmed =
            TrimWhitespace(StripLineComment(lines[function_line]));
        if (!StartsWith(function_trimmed, "function ") ||
            function_trimmed.find("focal_distance, clear_distance, closer_distance, "
                                  "farther_distance, near_strength, far_strength, alpha") ==
                std::string_view::npos) {
            continue;
        }

        const std::string function_indent(GetLuaLineIndent(lines[function_line]));
        std::size_t next_function_line = lines.size();
        for (std::size_t scan_line = function_line + 1; scan_line < lines.size();
             ++scan_line) {
            const std::string_view scan_trimmed =
                TrimWhitespace(StripLineComment(lines[scan_line]));
            if (GetLuaLineIndent(lines[scan_line]) == function_indent &&
                StartsWith(scan_trimmed, "function ")) {
                next_function_line = scan_line;
                break;
            }
        }
        if (next_function_line == lines.size() ||
            next_function_line <= function_line + 8 ||
            next_function_line - function_line > 80) {
            continue;
        }

        if (!line_contains(lines,
                           function_line,
                           next_function_line,
                           "local to_return = {}") ||
            !line_contains(lines,
                           function_line,
                           next_function_line,
                           "if not focal_distance - clear_distance then") ||
            !line_contains(lines,
                           function_line,
                           next_function_line,
                           "to_return.NearFocalPlane = math.max( (0.5))") ||
            !line_contains(lines,
                           function_line,
                           next_function_line,
                           "to_return.FarFocalPlane = (focal_distance + clear_distance)") ||
            !line_contains(lines,
                           function_line,
                           next_function_line,
                           "if alpha then") ||
            !line_contains(lines,
                           function_line,
                           next_function_line,
                           "to_return.Alpha = (0)")) {
            continue;
        }

        const std::string body_indent = function_indent + "  ";
        std::vector<std::string> replacement;
        replacement.push_back(std::string(lines[function_line]));
        replacement.push_back(body_indent + "local to_return = {}");
        replacement.push_back(body_indent + "to_return.NearBlur = near_strength or 0");
        replacement.push_back(
            body_indent +
            "to_return.NearFocalPlane = math.max(focal_distance and clear_distance and "
            "focal_distance - clear_distance or CameraValues.DOFDistance.GAME_MID, "
            "0.5)");
        replacement.push_back(
            body_indent +
            "to_return.NearPlane = math.max(to_return.NearFocalPlane - "
            "(closer_distance or CameraValues.DOFDistance.GAME_MID), 0.25)");
        replacement.push_back(body_indent + "to_return.FarBlur = far_strength or 0");
        replacement.push_back(
            body_indent +
            "to_return.FarFocalPlane = focal_distance and clear_distance and "
            "focal_distance + clear_distance or CameraValues.DOFDistance.GAME_MID");
        replacement.push_back(
            body_indent +
            "to_return.FarPlane = math.max(to_return.FarFocalPlane + "
            "(farther_distance or CameraValues.DOFDistance.GAME_MID), "
            "to_return.NearPlane)");
        replacement.push_back(
            body_indent +
            "to_return.Alpha = alpha or (0 < to_return.NearBlur or "
            "to_return.FarBlur) and 1 or 0");
        replacement.push_back(body_indent + "return to_return");
        replacement.push_back(function_indent + "end");

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(function_line),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(next_function_line));
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(function_line),
            replacement.begin(),
            replacement.end());

        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_malformed_dof_assembly_condition_expressions",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_malformed_dof_assembly_condition_expressions_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactBranchScopedRedeclaredLocal(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        context.mismatch.constant_delta < 0 ||
        context.mismatch.constant_delta > 2 ||
        context.mismatch.local_delta >= 0 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4 || lines.size() > 12000) {
        return false;
    }

    for (std::size_t line_index = 1; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &lhs,
                                                &rhs) ||
            is_local ||
            !refii::lua::IsValidLuaIdentifier(lhs) ||
            rhs.empty()) {
            continue;
        }

        const std::string line_indent(GetLuaLineIndent(lines[line_index]));
        if (line_indent.empty()) {
            continue;
        }

        std::size_t function_line = std::numeric_limits<std::size_t>::max();
        for (std::size_t scan_line = line_index; scan_line > 0; --scan_line) {
            const std::size_t candidate = scan_line - 1;
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[candidate]));
            if (StartsWith(trimmed, "function ")) {
                function_line = candidate;
                break;
            }
        }
        if (function_line == std::numeric_limits<std::size_t>::max() ||
            line_index - function_line > 160) {
            continue;
        }

        const std::string function_indent(GetLuaLineIndent(lines[function_line]));
        if (line_indent.size() <= function_indent.size()) {
            continue;
        }

        bool saw_branch_boundary = false;
        bool saw_prior_local = false;
        for (std::size_t scan_line = function_line + 1; scan_line < line_index;
             ++scan_line) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[scan_line]));
            if (trimmed == "end" ||
                StartsWith(trimmed, "else") ||
                StartsWith(trimmed, "elseif ")) {
                saw_branch_boundary = true;
            }
            if (StartsWith(trimmed, "local " + lhs + " = ") ||
                trimmed == "local " + lhs) {
                saw_prior_local = true;
            }
        }
        if (!saw_branch_boundary || !saw_prior_local) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] = line_indent + "local " + lhs + " = " + rhs;
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_branch_scoped_redeclared_local",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_branch_scoped_redeclared_local_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactFoldSeedLocalIntoMultiAssignmentCall(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 32 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 3 || lines.size() > 12000) {
        return false;
    }

    const auto replace_identifier =
        [](std::string_view expression,
           std::string_view identifier,
           std::string_view replacement,
           std::string* output) {
            if (output == nullptr || identifier.empty()) {
                return false;
            }
            std::string transformed;
            transformed.reserve(expression.size() + replacement.size());
            bool changed = false;
            char quote = '\0';
            bool escaped = false;
            for (std::size_t index = 0; index < expression.size();) {
                const char ch = expression[index];
                if (quote != '\0') {
                    transformed.push_back(ch);
                    if (escaped) {
                        escaped = false;
                    } else if (ch == '\\') {
                        escaped = true;
                    } else if (ch == quote) {
                        quote = '\0';
                    }
                    ++index;
                    continue;
                }
                if (ch == '\'' || ch == '"') {
                    quote = ch;
                    transformed.push_back(ch);
                    ++index;
                    continue;
                }
                if (index + identifier.size() <= expression.size() &&
                    expression.substr(index, identifier.size()) == identifier) {
                    const bool left_ok =
                        index == 0 || !IsIdentifierChar(expression[index - 1]);
                    const std::size_t right = index + identifier.size();
                    const bool right_ok =
                        right >= expression.size() || !IsIdentifierChar(expression[right]);
                    if (left_ok && right_ok) {
                        transformed.append(replacement);
                        index += identifier.size();
                        changed = true;
                        continue;
                    }
                }
                transformed.push_back(ch);
                ++index;
            }
            if (!changed || quote != '\0') {
                return false;
            }
            *output = std::move(transformed);
            return true;
        };

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        std::string seed_name;
        std::string seed_rhs;
        if (!ParseBytecodeRepairLocalInitializer(lines[line_index],
                                                 &seed_name,
                                                 &seed_rhs) ||
            !refii::lua::IsValidLuaIdentifier(seed_name)) {
            continue;
        }
        if (GetLuaLineIndent(lines[line_index]) !=
            GetLuaLineIndent(lines[line_index + 1])) {
            continue;
        }

        bool assignment_is_local = false;
        std::vector<std::string> lhs_names;
        std::string assignment_rhs;
        if (!refii::lua::ParseRenderedAssignmentLine(lines[line_index + 1],
                                                     &assignment_is_local,
                                                     &lhs_names,
                                                     &assignment_rhs) ||
            assignment_is_local ||
            lhs_names.size() < 2 ||
            assignment_rhs.find('(') == std::string::npos ||
            assignment_rhs.back() != ')') {
            continue;
        }

        bool lhs_contains_seed = false;
        std::string joined_lhs;
        for (std::size_t lhs_index = 0; lhs_index < lhs_names.size(); ++lhs_index) {
            lhs_names[lhs_index] =
                std::string(refii::lua::TrimLuaWhitespace(lhs_names[lhs_index]));
            if (!refii::lua::IsValidLuaIdentifier(lhs_names[lhs_index])) {
                lhs_contains_seed = false;
                break;
            }
            if (lhs_names[lhs_index] == seed_name) {
                lhs_contains_seed = true;
            }
            if (lhs_index != 0) {
                joined_lhs += ", ";
            }
            joined_lhs += lhs_names[lhs_index];
        }
        if (!lhs_contains_seed || joined_lhs.empty()) {
            continue;
        }

        std::string folded_rhs;
        if (!replace_identifier(assignment_rhs,
                                seed_name,
                                StripBytecodeRepairWholeParens(seed_rhs),
                                &folded_rhs)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        const std::string indent(GetLuaLineIndent(lines[line_index]));
        transformed_lines[line_index] =
            indent + "local " + joined_lhs + " = " + folded_rhs;
        transformed_lines.erase(
            transformed_lines.begin() +
            static_cast<std::ptrdiff_t>(line_index + 1));

        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_fold_seed_local_into_multi_assignment_call",
                candidate_source) ||
            TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_fold_seed_local_into_multi_assignment_call_exact",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactSingletonTableOverwriteToFieldPatch(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto parse_singleton_table_literal =
        [](std::string_view rhs,
           std::string* key_out,
           std::string* value_out,
           bool* key_is_identifier_out) {
            if (key_out == nullptr || value_out == nullptr ||
                key_is_identifier_out == nullptr) {
                return false;
            }
            const std::string_view trimmed = TrimWhitespace(rhs);
            if (trimmed.size() < 3 || trimmed.front() != '{' || trimmed.back() != '}') {
                return false;
            }
            const std::string_view body =
                TrimWhitespace(trimmed.substr(1, trimmed.size() - 2));
            if (body.empty() || body.find(',') != std::string_view::npos) {
                return false;
            }
            const std::size_t equals_pos = body.find('=');
            if (equals_pos == std::string_view::npos ||
                equals_pos == 0 ||
                equals_pos + 1 >= body.size()) {
                return false;
            }
            const std::string_view key =
                TrimWhitespace(body.substr(0, equals_pos));
            const std::string_view value =
                TrimWhitespace(body.substr(equals_pos + 1));
            if (key.empty() || value.empty()) {
                return false;
            }

            bool key_is_identifier = false;
            if (refii::lua::IsValidLuaIdentifier(key)) {
                key_is_identifier = true;
            } else if (key.size() >= 3 && key.front() == '[' && key.back() == ']') {
                const std::string_view inner =
                    TrimWhitespace(key.substr(1, key.size() - 2));
                if (inner.empty()) {
                    return false;
                }
            } else {
                return false;
            }

            *key_out = std::string(key);
            *value_out = std::string(value);
            *key_is_identifier_out = key_is_identifier;
            return true;
        };

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[line_index],
                                                 &first_is_local,
                                                 &first_lhs,
                                                 &first_rhs) ||
            first_is_local || first_rhs.empty() || first_rhs == "{}" ||
            first_rhs.front() == '{' || first_rhs.find("function") != std::string::npos) {
            continue;
        }

        std::size_t second_line = line_index + 1;
        while (second_line < lines.size() &&
               TrimWhitespace(StripLineComment(lines[second_line])).empty()) {
            ++second_line;
        }
        if (second_line >= lines.size()) {
            continue;
        }

        bool second_is_local = false;
        std::string second_lhs;
        std::string second_rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[second_line],
                                                 &second_is_local,
                                                 &second_lhs,
                                                 &second_rhs) ||
            second_is_local || second_lhs != first_lhs) {
            continue;
        }

        std::string table_key;
        std::string table_value;
        bool key_is_identifier = false;
        if (!parse_singleton_table_literal(second_rhs,
                                           &table_key,
                                           &table_value,
                                           &key_is_identifier)) {
            continue;
        }

        std::string patched_lhs = first_lhs;
        if (key_is_identifier) {
            patched_lhs += "." + table_key;
        } else {
            patched_lhs += table_key;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[second_line] =
            GetLuaLineIndent(lines[second_line]) + patched_lhs + " = " + table_value;
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_near_exact_singleton_overwrite_patch",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactSingleUseLocalTableCallLiteral(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto parse_local_table_seed =
        [](std::string_view line, std::string* local_name, std::string* table_literal) {
            if (local_name == nullptr || table_literal == nullptr) {
                return false;
            }
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (!StartsWith(trimmed, "local ")) {
                return false;
            }
            const std::string_view local_clause = TrimWhitespace(trimmed.substr(6));
            const std::size_t equals_pos = local_clause.find('=');
            if (equals_pos == std::string_view::npos) {
                return false;
            }
            const std::string_view lhs =
                TrimWhitespace(local_clause.substr(0, equals_pos));
            const std::string_view rhs =
                TrimWhitespace(local_clause.substr(equals_pos + 1));
            if (!refii::lua::IsValidLuaIdentifier(lhs) || rhs.size() < 2 ||
                rhs.front() != '{' || rhs.back() != '}' ||
                rhs == "{}") {
                return false;
            }
            *local_name = std::string(lhs);
            *table_literal = std::string(rhs);
            return true;
        };

    const auto count_identifier_uses =
        [&](std::size_t start_line, std::string_view identifier) {
            std::size_t uses = 0;
            for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
                const std::string_view stripped = StripLineComment(lines[line_index]);
                std::size_t pos = 0;
                while (pos < stripped.size()) {
                    pos = stripped.find(identifier, pos);
                    if (pos == std::string_view::npos) {
                        break;
                    }
                    const bool left_ok =
                        pos == 0 || !IsIdentifierChar(stripped[pos - 1]);
                    const std::size_t right = pos + identifier.size();
                    const bool right_ok =
                        right >= stripped.size() || !IsIdentifierChar(stripped[right]);
                    if (left_ok && right_ok) {
                        ++uses;
                    }
                    pos = right;
                }
            }
            return uses;
        };

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        std::string local_name;
        std::string table_literal;
        if (!parse_local_table_seed(lines[line_index], &local_name, &table_literal)) {
            continue;
        }

        const std::size_t next_line = line_index + 1;
        const std::string_view next_trimmed =
            TrimWhitespace(StripLineComment(lines[next_line]));
        if (next_trimmed.empty() || next_trimmed.find("{}") == std::string_view::npos ||
            next_trimmed.find('(') == std::string_view::npos ||
            next_trimmed.find(')') == std::string_view::npos ||
            next_trimmed.find('=') != std::string_view::npos) {
            continue;
        }

        if (count_identifier_uses(next_line, local_name) != 0) {
            continue;
        }

        const std::size_t placeholder_pos = lines[next_line].find("{}");
        if (placeholder_pos == std::string::npos) {
            continue;
        }
        std::vector<std::string> transformed_lines = lines;
        transformed_lines[next_line].replace(placeholder_pos, 2, table_literal);
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(line_index));
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_near_exact_single_use_table_call_inline",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactIndexedTableOverwriteLiteralChain(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr ||
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 12 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 8) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> original_lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (original_lines.empty()) {
        return false;
    }

    const auto parse_indexed_target = [&](std::string_view expression,
                                         std::string* base_out,
                                         std::string* index_out) {
        expression = TrimWhitespace(expression);
        if (expression.size() < 4 || expression.back() != ']') {
            return false;
        }

        bool in_string = false;
        char quote = '\0';
        int depth = 0;
        std::size_t open = std::string_view::npos;
        for (std::size_t cursor = expression.size(); cursor > 0;) {
            --cursor;
            const char ch = expression[cursor];
            if (in_string) {
                if (ch == quote && (cursor == 0 || expression[cursor - 1] != '\\')) {
                    in_string = false;
                }
                continue;
            }
            if (ch == '"' || ch == '\'') {
                in_string = true;
                quote = ch;
                continue;
            }
            if (ch == ']') {
                ++depth;
                continue;
            }
            if (ch == '[') {
                --depth;
                if (depth == 0) {
                    open = cursor;
                    break;
                }
            }
        }
        if (open == std::string_view::npos || open == 0) {
            return false;
        }

        const std::string base =
            StripBytecodeRepairWholeParens(expression.substr(0, open));
        const std::string index =
            StripBytecodeRepairWholeParens(
                expression.substr(open + 1, expression.size() - open - 2));
        if (!IsBytecodeRepairSafeDottedPrefix(base) || index.empty()) {
            return false;
        }
        if (base_out != nullptr) {
            *base_out = base;
        }
        if (index_out != nullptr) {
            *index_out = index;
        }
        return true;
    };

    const auto apply_best_candidate_loop_shape = [](std::vector<std::string>* candidate_lines) {
        if (candidate_lines == nullptr) {
            return false;
        }

        bool has_favourite_local = false;
        bool has_value_loop = false;
        for (const std::string& line : *candidate_lines) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            has_favourite_local |= trimmed == "local favourite";
            has_value_loop |= trimmed.find(" value in pairs(") != std::string_view::npos;
        }
        if (!has_favourite_local || !has_value_loop) {
            return false;
        }

        bool changed = false;
        for (std::string& line : *candidate_lines) {
            const std::string indent(GetLuaLineIndent(line));
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (trimmed == "if favourite.Priority < value.Priority then") {
                line = indent + "if value.Priority > favourite.Priority then";
                changed = true;
            } else if (trimmed == "elseif favourite.Priority == favourite.Priority then") {
                line = indent + "elseif value.Priority == favourite.Priority then";
                changed = true;
            } else if (trimmed == "if favourite.Time < value.Time then") {
                line = indent + "if value.Time > favourite.Time then";
                changed = true;
            }
        }

        for (std::size_t line_index = 0; line_index + 5 < candidate_lines->size();
             ++line_index) {
            const std::string indent(GetLuaLineIndent((*candidate_lines)[line_index]));
            if (TrimWhitespace(StripLineComment((*candidate_lines)[line_index])) !=
                    "if not favourite or not GetIDFromEntity(favourite.Entity) then" ||
                TrimWhitespace(StripLineComment((*candidate_lines)[line_index + 1])) !=
                    "end" ||
                TrimWhitespace(StripLineComment((*candidate_lines)[line_index + 2])) !=
                    "self.CurrentLevelOverrideID = GetIDFromEntity(favourite.Entity)" ||
                TrimWhitespace(StripLineComment((*candidate_lines)[line_index + 3])) !=
                    "if not favourite or not CameraValues.GetCameraValues(favourite.Entity) then" ||
                TrimWhitespace(StripLineComment((*candidate_lines)[line_index + 4])) !=
                    "end" ||
                TrimWhitespace(StripLineComment((*candidate_lines)[line_index + 5])) !=
                    "self.CurrentLevelOverrideValues = CameraValues.GetCameraValues(favourite.Entity)") {
                continue;
            }

            (*candidate_lines)[line_index] =
                indent +
                "self.CurrentLevelOverrideID = (favourite and "
                "GetIDFromEntity(favourite.Entity)) or nil";
            (*candidate_lines)[line_index + 1] =
                indent +
                "self.CurrentLevelOverrideValues = (favourite and "
                "CameraValues.GetCameraValues(favourite.Entity)) or nil";
            candidate_lines->erase(
                candidate_lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2),
                candidate_lines->begin() + static_cast<std::ptrdiff_t>(line_index + 6));
            changed = true;
            break;
        }

        return changed;
    };

    struct LiteralEntry {
        std::string key;
        std::string rhs;
        bool from_indexed_target = false;
    };

    for (std::size_t line_index = 0; line_index < original_lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseBytecodeRepairSingleAssignment(original_lines[line_index],
                                                 &is_local,
                                                 &lhs_expression,
                                                 &rhs_expression) ||
            is_local ||
            StripBytecodeRepairWholeParens(rhs_expression) != "{}") {
            continue;
        }

        const std::string indexed_target =
            StripBytecodeRepairWholeParens(lhs_expression);
        std::string base_expression;
        std::string index_expression;
        if (!parse_indexed_target(indexed_target, &base_expression, &index_expression)) {
            continue;
        }

        const std::string indent(GetLuaLineIndent(original_lines[line_index]));
        std::vector<LiteralEntry> reversed_entries;
        std::size_t start_line = line_index;
        bool saw_indexed_field = false;
        bool saw_base_field = false;
        bool unsafe = false;
        for (std::size_t probe = line_index; probe > 0 && reversed_entries.size() < 8;) {
            --probe;
            const std::string_view stripped = StripLineComment(original_lines[probe]);
            if (TrimWhitespace(stripped).empty()) {
                continue;
            }
            if (GetLuaLineIndent(original_lines[probe]) != indent) {
                break;
            }

            bool field_is_function = false;
            int array_index = -1;
            LiteralEntry entry;
            if (refii::lua::ParseRenderedTableFieldAssignment(original_lines[probe],
                                                              indexed_target,
                                                              &entry.key,
                                                              &entry.rhs,
                                                              &field_is_function,
                                                              &array_index)) {
                entry.from_indexed_target = true;
                saw_indexed_field = true;
            } else if (refii::lua::ParseRenderedTableFieldAssignment(original_lines[probe],
                                                                     base_expression,
                                                                     &entry.key,
                                                                     &entry.rhs,
                                                                     &field_is_function,
                                                                     &array_index)) {
                saw_base_field = true;
            } else {
                break;
            }

            if (entry.key.empty() || entry.key.front() == '[' || field_is_function ||
                entry.rhs.empty() ||
                refii::lua::CountIdentifierUses(entry.rhs, indexed_target) != 0) {
                unsafe = true;
                break;
            }
            reversed_entries.push_back(std::move(entry));
            start_line = probe;
        }

        if (unsafe || reversed_entries.empty() || !saw_indexed_field || !saw_base_field) {
            continue;
        }

        std::vector<LiteralEntry> entries(reversed_entries.rbegin(),
                                          reversed_entries.rend());
        std::string literal = "{";
        for (std::size_t entry_index = 0; entry_index < entries.size(); ++entry_index) {
            if (entry_index != 0) {
                literal += ", ";
            }
            literal += entries[entry_index].key;
            literal += " = ";
            literal += entries[entry_index].rhs;
        }
        literal += "}";

        std::vector<std::string> transformed_lines = original_lines;
        transformed_lines[start_line] = indent + indexed_target + " = " + literal;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(start_line + 1),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        apply_best_candidate_loop_shape(&transformed_lines);
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_indexed_table_literal_chain",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactLastMessageIteratorSeed(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr ||
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 5 < lines.size(); ++line_index) {
        const std::string indent(GetLuaLineIndent(lines[line_index]));
        if (TrimWhitespace(StripLineComment(lines[line_index])) !=
                "if not self.LastMessage then" ||
            TrimWhitespace(StripLineComment(lines[line_index + 1])) !=
                "local r2 = 0" ||
            TrimWhitespace(StripLineComment(lines[line_index + 2])) != "end" ||
            TrimWhitespace(StripLineComment(lines[line_index + 3])) !=
                "self.LastMessage = self.LastMessage" ||
            TrimWhitespace(StripLineComment(lines[line_index + 4])) !=
                "local changed = nil") {
            continue;
        }

        std::size_t for_line = line_index + 5;
        while (for_line < lines.size() &&
               TrimWhitespace(StripLineComment(lines[for_line])).empty()) {
            ++for_line;
        }
        if (for_line >= lines.size()) {
            continue;
        }
        const std::string_view for_trimmed = TrimWhitespace(StripLineComment(lines[for_line]));
        constexpr std::string_view kForPrefix =
            "for _, msg in ipairs(MessageEvents.GetAllMessages(";
        constexpr std::string_view kForSuffix = ", self.LastMessage)) do";
        if (!for_trimmed.starts_with(kForPrefix) || !for_trimmed.ends_with(kForSuffix)) {
            continue;
        }
        const std::string event_expression = std::string(TrimWhitespace(
            for_trimmed.substr(kForPrefix.size(),
                               for_trimmed.size() - kForPrefix.size() - kForSuffix.size())));
        if (event_expression.empty()) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            indent + "self.LastMessage = (self.LastMessage or 0)";
        transformed_lines[line_index + 1] = indent + "local changed";
        transformed_lines[line_index + 2] =
            indent + "local messages = MessageEvents.GetAllMessages(" +
            event_expression + ", self.LastMessage)";
        transformed_lines[line_index + 3] = indent + "for _, msg in ipairs(messages) do";
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 4),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(for_line + 1));
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_last_message_iterator_seed",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactActionTableSeedAndCallLocalReuse(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    struct TableField {
        std::string key;
        std::string value;
    };

    const auto get_rk_constant =
        [](const refii::lua::Lua51Function& function,
           int rk) -> const refii::lua::Lua51Constant* {
        constexpr int kLua51RkConstantFlag = 256;
        if (rk < kLua51RkConstantFlag) {
            return nullptr;
        }
        const int constant_index = rk - kLua51RkConstantFlag;
        if (constant_index < 0 ||
            static_cast<std::size_t>(constant_index) >= function.constants.size()) {
            return nullptr;
        }
        return &function.constants[static_cast<std::size_t>(constant_index)];
    };

    const auto render_constant = [](const refii::lua::Lua51Constant& constant) {
        return refii::lua::RenderLuaConstant(constant);
    };

    const auto render_table_key =
        [&](const refii::lua::Lua51Constant& constant, std::string* key_out) {
        if (key_out == nullptr ||
            constant.type != refii::lua::Lua51ConstantType::kString ||
            constant.string_value.empty()) {
            return false;
        }
        if (refii::lua::IsValidLuaIdentifier(constant.string_value)) {
            *key_out = constant.string_value;
        } else {
            *key_out = "[" + render_constant(constant) + "]";
        }
        return true;
    };

    const auto collect_action_table_seed_candidates =
        [&]() {
        std::vector<std::vector<TableField>> candidates;
        const refii::lua::Lua51Function* preferred_function =
            GetLuaFunctionByPreorderIndex(context.original_chunk,
                                          context.mismatch.function_index);
        std::vector<const refii::lua::Lua51Function*> functions;
        if (preferred_function != nullptr) {
            functions.push_back(preferred_function);
        }
        for (std::size_t function_index = 1;; ++function_index) {
            const refii::lua::Lua51Function* function =
                GetLuaFunctionByPreorderIndex(context.original_chunk, function_index);
            if (function == nullptr) {
                break;
            }
            if (function != preferred_function) {
                functions.push_back(function);
            }
        }

        for (const refii::lua::Lua51Function* function : functions) {
            if (function == nullptr) {
                continue;
            }
            const std::vector<refii::lua::Lua51Instruction> instructions =
                refii::lua::DecodeLua51Instructions(*function);
            for (std::size_t index = 0; index < instructions.size(); ++index) {
                const refii::lua::Lua51Instruction& seed = instructions[index];
                if (seed.opcode != refii::lua::Lua51Opcode::kNewTable) {
                    continue;
                }

                std::unordered_map<int, std::string> register_expressions;
                std::vector<TableField> fields;
                bool saw_type_field = false;
                for (std::size_t cursor = index + 1;
                     cursor < instructions.size() && cursor <= index + 16;
                     ++cursor) {
                    const refii::lua::Lua51Instruction& instruction =
                        instructions[cursor];
                    switch (instruction.opcode) {
                    case refii::lua::Lua51Opcode::kLoadK: {
                        if (instruction.bx >= 0 &&
                            static_cast<std::size_t>(instruction.bx) <
                                function->constants.size()) {
                            register_expressions[instruction.a] = render_constant(
                                function->constants[static_cast<std::size_t>(
                                    instruction.bx)]);
                        }
                        break;
                    }
                    case refii::lua::Lua51Opcode::kLoadBool:
                        register_expressions[instruction.a] =
                            instruction.b != 0 ? "true" : "false";
                        break;
                    case refii::lua::Lua51Opcode::kGetGlobal: {
                        if (instruction.bx >= 0 &&
                            static_cast<std::size_t>(instruction.bx) <
                                function->constants.size()) {
                            const refii::lua::Lua51Constant& constant =
                                function->constants[static_cast<std::size_t>(
                                    instruction.bx)];
                            if (constant.type == refii::lua::Lua51ConstantType::kString &&
                                refii::lua::IsValidLuaIdentifier(constant.string_value)) {
                                register_expressions[instruction.a] =
                                    constant.string_value;
                            }
                        }
                        break;
                    }
                    case refii::lua::Lua51Opcode::kMove: {
                        const auto it = register_expressions.find(instruction.b);
                        if (it != register_expressions.end()) {
                            register_expressions[instruction.a] = it->second;
                        }
                        break;
                    }
                    case refii::lua::Lua51Opcode::kGetTable: {
                        std::string base;
                        const auto base_it = register_expressions.find(instruction.b);
                        if (base_it != register_expressions.end()) {
                            base = base_it->second;
                        } else if (instruction.b == 0) {
                            base = "self";
                        }
                        const refii::lua::Lua51Constant* key_constant =
                            get_rk_constant(*function, instruction.c);
                        std::string key;
                        if (!base.empty() && key_constant != nullptr &&
                            render_table_key(*key_constant, &key)) {
                            if (!key.empty() && key.front() == '[') {
                                register_expressions[instruction.a] = base + key;
                            } else {
                                register_expressions[instruction.a] = base + "." + key;
                            }
                        }
                        break;
                    }
                    case refii::lua::Lua51Opcode::kSetTable: {
                        if (instruction.a != seed.a) {
                            if (!fields.empty()) {
                                cursor = instructions.size();
                            }
                            break;
                        }
                        const refii::lua::Lua51Constant* key_constant =
                            get_rk_constant(*function, instruction.b);
                        std::string key;
                        if (key_constant == nullptr ||
                            !render_table_key(*key_constant, &key)) {
                            break;
                        }

                        std::string value;
                        if (const refii::lua::Lua51Constant* value_constant =
                                get_rk_constant(*function, instruction.c);
                            value_constant != nullptr) {
                            value = render_constant(*value_constant);
                        } else {
                            const auto value_it =
                                register_expressions.find(instruction.c);
                            if (value_it != register_expressions.end()) {
                                value = value_it->second;
                            }
                        }
                        if (value.empty()) {
                            break;
                        }
                        saw_type_field = saw_type_field || key == "Type";
                        fields.push_back(TableField{std::move(key), std::move(value)});
                        break;
                    }
                    default:
                        if (!fields.empty()) {
                            cursor = instructions.size();
                        }
                        break;
                    }
                }

                if (saw_type_field && fields.size() >= 2) {
                    candidates.push_back(std::move(fields));
                }
            }
        }
        return candidates;
    };

    const auto parse_singleton_table_literal =
        [](std::string_view literal,
           std::string* key_out,
           std::string* value_out) {
        if (key_out == nullptr || value_out == nullptr) {
            return false;
        }
        literal = TrimWhitespace(literal);
        if (literal.size() < 5 || literal.front() != '{' ||
            literal.back() != '}' || literal == "{}") {
            return false;
        }
        std::string_view body = TrimWhitespace(literal.substr(1, literal.size() - 2));
        const std::size_t equals_pos = body.find('=');
        if (equals_pos == std::string_view::npos || equals_pos == 0 ||
            equals_pos + 1 >= body.size() ||
            body.find(',', equals_pos + 1) != std::string_view::npos) {
            return false;
        }
        const std::string key = std::string(TrimWhitespace(body.substr(0, equals_pos)));
        const std::string value =
            std::string(TrimWhitespace(body.substr(equals_pos + 1)));
        if (key.empty() || value.empty()) {
            return false;
        }
        *key_out = key;
        *value_out = value;
        return true;
    };

    const auto build_literal = [](const std::vector<TableField>& fields) {
        std::string literal = "{";
        for (std::size_t index = 0; index < fields.size(); ++index) {
            if (index != 0) {
                literal += ", ";
            }
            literal += fields[index].key;
            literal += " = ";
            literal += fields[index].value;
        }
        literal += "}";
        return literal;
    };

    auto apply_branch_seed_repair =
        [&](std::vector<std::string>* candidate_lines) {
        if (candidate_lines == nullptr) {
            return false;
        }
        const std::vector<std::vector<TableField>> seed_candidates =
            collect_action_table_seed_candidates();
        if (seed_candidates.empty()) {
            return false;
        }

        for (std::size_t line_index = 0; line_index + 4 < candidate_lines->size();
             ++line_index) {
            const std::string_view if_trimmed =
                TrimWhitespace(StripLineComment((*candidate_lines)[line_index]));
            if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then")) {
                continue;
            }

            bool then_is_local = false;
            std::string then_lhs;
            std::string then_rhs;
            if (!ParseBytecodeRepairSingleAssignment((*candidate_lines)[line_index + 1],
                                                     &then_is_local,
                                                     &then_lhs,
                                                     &then_rhs) ||
                !then_is_local || !refii::lua::IsValidLuaIdentifier(then_lhs)) {
                continue;
            }
            std::string branch_key;
            std::string then_value;
            if (!parse_singleton_table_literal(then_rhs, &branch_key, &then_value)) {
                continue;
            }

            const std::size_t else_line = line_index + 2;
            if (TrimWhitespace(StripLineComment((*candidate_lines)[else_line])) != "else") {
                continue;
            }
            bool else_is_local = false;
            std::string else_lhs;
            std::string else_rhs;
            if (!ParseBytecodeRepairSingleAssignment((*candidate_lines)[else_line + 1],
                                                     &else_is_local,
                                                     &else_lhs,
                                                     &else_rhs) ||
                else_is_local || else_lhs != then_lhs) {
                continue;
            }
            std::string else_key;
            std::string else_value;
            if (!parse_singleton_table_literal(else_rhs, &else_key, &else_value) ||
                else_key != branch_key) {
                continue;
            }
            if (TrimWhitespace(StripLineComment((*candidate_lines)[else_line + 2])) !=
                "end") {
                continue;
            }

            bool passed_to_action = false;
            const std::size_t scan_limit =
                std::min<std::size_t>(candidate_lines->size(), else_line + 24);
            for (std::size_t probe = else_line + 3; probe < scan_limit; ++probe) {
                const std::string_view probe_trimmed =
                    TrimWhitespace(StripLineComment((*candidate_lines)[probe]));
                if (probe_trimmed.find("Action.SetCurrentAction(") !=
                        std::string_view::npos &&
                    refii::lua::CountIdentifierUses(probe_trimmed, then_lhs) != 0) {
                    passed_to_action = true;
                    break;
                }
                if (probe_trimmed.starts_with("local " + then_lhs) ||
                    probe_trimmed.starts_with(then_lhs + " =")) {
                    break;
                }
            }
            if (!passed_to_action) {
                continue;
            }

            for (const std::vector<TableField>& seed_fields : seed_candidates) {
                bool seed_has_branch_key = false;
                bool seed_has_type = false;
                for (const TableField& field : seed_fields) {
                    seed_has_branch_key |= field.key == branch_key;
                    seed_has_type |= field.key == "Type";
                }
                if (seed_has_branch_key || !seed_has_type) {
                    continue;
                }

                const std::string indent = GetLuaLineIndent((*candidate_lines)[line_index]);
                candidate_lines->insert(
                    candidate_lines->begin() +
                        static_cast<std::ptrdiff_t>(line_index),
                    indent + "local " + then_lhs + " = " + build_literal(seed_fields));
                (*candidate_lines)[line_index + 2] =
                    GetLuaLineIndent((*candidate_lines)[line_index + 2]) + then_lhs +
                    "." + branch_key + " = " + then_value;
                (*candidate_lines)[line_index + 4] =
                    GetLuaLineIndent((*candidate_lines)[line_index + 4]) + then_lhs +
                    "." + branch_key + " = " + else_value;
                return true;
            }
        }

        return false;
    };

    auto apply_action_call_local_reuse =
        [&](std::vector<std::string>* candidate_lines) {
        if (candidate_lines == nullptr) {
            return false;
        }
        bool changed = false;
        for (std::size_t line_index = 0; line_index + 1 < candidate_lines->size();
             ++line_index) {
            bool seed_is_local = false;
            std::string seed_lhs;
            std::string seed_rhs;
            if (!ParseBytecodeRepairSingleAssignment((*candidate_lines)[line_index],
                                                     &seed_is_local,
                                                     &seed_lhs,
                                                     &seed_rhs) ||
                !seed_is_local || !refii::lua::IsValidLuaIdentifier(seed_lhs) ||
                seed_rhs.size() < 5 || seed_rhs.front() != '{' ||
                seed_rhs.back() != '}' ||
                seed_rhs.find("EScriptableAction.") == std::string::npos) {
                continue;
            }

            std::size_t call_line = line_index + 1;
            while (call_line < candidate_lines->size() &&
                   TrimWhitespace(StripLineComment((*candidate_lines)[call_line]))
                       .empty()) {
                ++call_line;
            }
            if (call_line >= candidate_lines->size()) {
                continue;
            }

            const std::string_view stripped =
                StripLineComment((*candidate_lines)[call_line]);
            const std::string_view trimmed = TrimWhitespace(stripped);
            if (trimmed.find("Action.SetCurrentAction(") == std::string_view::npos ||
                trimmed.find(seed_lhs) != std::string_view::npos) {
                continue;
            }
            const std::size_t table_open = stripped.find('{');
            if (table_open == std::string_view::npos) {
                continue;
            }
            const std::size_t table_close =
                FindBytecodeRepairMatchingBrace(stripped, table_open);
            if (table_close == std::string_view::npos || table_close <= table_open + 1) {
                continue;
            }
            const std::string full_literal =
                std::string(stripped.substr(table_open, table_close - table_open + 1));
            if (full_literal.find("EScriptableAction.") == std::string::npos ||
                full_literal.find('=') == std::string::npos) {
                continue;
            }

            std::string seed_type_value;
            {
                std::string seed_key;
                std::string seed_value;
                if (parse_singleton_table_literal(seed_rhs, &seed_key, &seed_value) &&
                    seed_key == "Type") {
                    seed_type_value = seed_value;
                } else {
                    const std::string needle = "Type = ";
                    const std::size_t type_pos = seed_rhs.find(needle);
                    if (type_pos != std::string::npos) {
                        const std::size_t value_start = type_pos + needle.size();
                        const std::size_t value_end = seed_rhs.find_first_of(",}", value_start);
                        seed_type_value = std::string(TrimWhitespace(
                            std::string_view(seed_rhs).substr(
                                value_start,
                                value_end == std::string::npos
                                    ? std::string_view::npos
                                    : value_end - value_start)));
                    }
                }
            }
            if (seed_type_value.empty() ||
                full_literal.find("Type = " + seed_type_value) == std::string::npos) {
                continue;
            }

            (*candidate_lines)[line_index] =
                GetLuaLineIndent((*candidate_lines)[line_index]) + "local " +
                seed_lhs + " = " + full_literal;
            (*candidate_lines)[call_line] =
                std::string(stripped.substr(0, table_open)) + seed_lhs +
                std::string(stripped.substr(table_close + 1));
            changed = true;
        }
        return changed;
    };

    std::vector<std::string> transformed_lines = lines;
    const bool branch_changed = apply_branch_seed_repair(&transformed_lines);
    const bool call_changed = apply_action_call_local_reuse(&transformed_lines);
    if ((branch_changed || call_changed) &&
        TryCommitBytecodeParityImprovingLines(
            context,
            transformed_lines,
            had_trailing_newline,
            "bytecode_parity_candidate_near_exact_action_table_seed_and_call_reuse",
            candidate_source)) {
        return true;
    }

    return false;
}

bool TryRepairNearExactLocalSelfTableLiteralFieldWrites(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 16 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[line_index],
                                                 &is_local,
                                                 &lhs,
                                                 &rhs) ||
            lhs != "self") {
            continue;
        }

        std::vector<BytecodeRepairParsedTableField> fields;
        if (!ParseBytecodeRepairTableLiteralFields(rhs, &fields)) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            indent + "self." + fields.front().key + " = " + fields.front().value;
        for (std::size_t field_index = 1; field_index < fields.size(); ++field_index) {
            transformed_lines.insert(
                transformed_lines.begin() +
                    static_cast<std::ptrdiff_t>(line_index + field_index),
                indent + "self." + fields[field_index].key + " = " +
                    fields[field_index].value);
        }

        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                is_local
                    ? "bytecode_parity_candidate_near_exact_local_self_table_field_writes"
                    : "bytecode_parity_candidate_near_exact_self_table_field_writes",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactConstructorDefaultObjectAndSelfIndex(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto line_trimmed = [&](std::size_t line_index) {
        return TrimWhitespace(StripLineComment(lines[line_index]));
    };

    for (std::size_t line_index = 1; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &lhs,
                                                &rhs) ||
            is_local || (rhs != "BaseObjects" && rhs != "{}") ||
            !refii::lua::IsValidLuaIdentifier(lhs)) {
            continue;
        }

        const std::string_view guard = line_trimmed(line_index - 1);
        if (guard != "if not " + lhs + " then") {
            continue;
        }

        std::size_t setmetatable_line = lines.size();
        std::size_t index_line = lines.size();
        std::size_t return_line = lines.size();
        std::size_t scan = line_index + 1;
        for (; scan < lines.size(); ++scan) {
            const std::string_view trimmed = line_trimmed(scan);
            if (trimmed.empty()) {
                continue;
            }
            if (StartsWith(trimmed, "function ") ||
                StartsWith(trimmed, "local function ")) {
                break;
            }
            if (trimmed == "setmetatable(" + lhs + ", self)") {
                setmetatable_line = scan;
                continue;
            }
            if (setmetatable_line != lines.size() &&
                trimmed == "self.__index = BaseObjects") {
                index_line = scan;
                continue;
            }
            if (setmetatable_line != lines.size() && trimmed == "return " + lhs) {
                return_line = scan;
                break;
            }
            if (trimmed == "end" && setmetatable_line == lines.size()) {
                break;
            }
        }

        if (setmetatable_line == lines.size() || index_line == lines.size() ||
            return_line == lines.size() || index_line > return_line) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        if (rhs == "BaseObjects") {
            transformed_lines[line_index] = GetLuaLineIndent(lines[line_index]) + lhs + " = {}";
        }
        transformed_lines[index_line] = GetLuaLineIndent(lines[index_line]) +
                                        "self.__index = self";

        for (std::size_t self_line = 0; self_line < transformed_lines.size(); ++self_line) {
            bool self_is_local = false;
            std::string self_lhs;
            std::string self_rhs;
            if (!ParseBytecodeRepairSingleAssignment(transformed_lines[self_line],
                                                     &self_is_local,
                                                     &self_lhs,
                                                     &self_rhs) ||
                self_is_local || self_lhs != "self") {
                continue;
            }

            std::vector<BytecodeRepairParsedTableField> fields;
            if (!ParseBytecodeRepairTableLiteralFields(self_rhs, &fields)) {
                continue;
            }

            const std::string indent = GetLuaLineIndent(transformed_lines[self_line]);
            transformed_lines[self_line] =
                indent + "self." + fields.front().key + " = " + fields.front().value;
            for (std::size_t field_index = 1; field_index < fields.size(); ++field_index) {
                transformed_lines.insert(
                    transformed_lines.begin() +
                        static_cast<std::ptrdiff_t>(self_line + field_index),
                    indent + "self." + fields[field_index].key + " = " +
                        fields[field_index].value);
            }
            self_line += fields.size() - 1;
        }

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_constructor_default_object_self_index",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_constructor_default_object_self_index",
                candidate_source)) {
            return true;
        }
    }

    for (std::size_t setmetatable_line = 0; setmetatable_line < lines.size();
         ++setmetatable_line) {
        const std::string setmetatable_trimmed =
            std::string(line_trimmed(setmetatable_line));
        constexpr std::string_view kSetmetatablePrefix = "setmetatable(";
        constexpr std::string_view kSetmetatableSuffix = ", self)";
        if (!StartsWith(setmetatable_trimmed, kSetmetatablePrefix) ||
            !EndsWith(setmetatable_trimmed, kSetmetatableSuffix)) {
            continue;
        }
        const std::string lhs = std::string(TrimWhitespace(std::string_view(setmetatable_trimmed)
                                                               .substr(kSetmetatablePrefix.size(),
                                                                       setmetatable_trimmed.size() -
                                                                           kSetmetatablePrefix.size() -
                                                                           kSetmetatableSuffix.size())));
        if (!refii::lua::IsValidLuaIdentifier(lhs) || setmetatable_line < 2) {
            continue;
        }

        std::size_t assignment_line = lines.size();
        for (std::size_t cursor = setmetatable_line; cursor > 0; --cursor) {
            const std::size_t probe = cursor - 1;
            const std::string probe_trimmed = std::string(line_trimmed(probe));
            if (probe_trimmed == lhs + " = BaseObjects" ||
                probe_trimmed == lhs + " = {}") {
                assignment_line = probe;
                break;
            }
            if (!probe_trimmed.empty() && probe_trimmed != "end") {
                break;
            }
        }
        if (assignment_line == lines.size() || assignment_line == 0 ||
            std::string(line_trimmed(assignment_line - 1)) != "if not " + lhs + " then") {
            continue;
        }

        std::size_t index_line = lines.size();
        std::size_t return_line = lines.size();
        for (std::size_t cursor = setmetatable_line + 1; cursor < lines.size(); ++cursor) {
            const std::string cursor_trimmed = std::string(line_trimmed(cursor));
            if (cursor_trimmed == "self.__index = BaseObjects") {
                index_line = cursor;
                continue;
            }
            if (cursor_trimmed == "return " + lhs) {
                return_line = cursor;
                break;
            }
            if (StartsWith(cursor_trimmed, "function ") ||
                StartsWith(cursor_trimmed, "local function ")) {
                break;
            }
        }
        if (index_line == lines.size() || return_line == lines.size()) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        if (std::string(line_trimmed(assignment_line)) == lhs + " = BaseObjects") {
            transformed_lines[assignment_line] =
                GetLuaLineIndent(lines[assignment_line]) + lhs + " = {}";
        }
        transformed_lines[index_line] =
            GetLuaLineIndent(lines[index_line]) + "self.__index = self";

        for (std::size_t self_line = 0; self_line < transformed_lines.size(); ++self_line) {
            bool self_is_local = false;
            std::string self_lhs;
            std::string self_rhs;
            if (!ParseBytecodeRepairSingleAssignment(transformed_lines[self_line],
                                                     &self_is_local,
                                                     &self_lhs,
                                                     &self_rhs) ||
                self_is_local || self_lhs != "self") {
                continue;
            }
            std::vector<BytecodeRepairParsedTableField> fields;
            if (!ParseBytecodeRepairTableLiteralFields(self_rhs, &fields)) {
                continue;
            }
            const std::string indent = GetLuaLineIndent(transformed_lines[self_line]);
            transformed_lines[self_line] =
                indent + "self." + fields.front().key + " = " + fields.front().value;
            for (std::size_t field_index = 1; field_index < fields.size(); ++field_index) {
                transformed_lines.insert(
                    transformed_lines.begin() +
                        static_cast<std::ptrdiff_t>(self_line + field_index),
                    indent + "self." + fields[field_index].key + " = " +
                        fields[field_index].value);
            }
            self_line += fields.size() - 1;
        }

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_constructor_default_object_self_index_scan",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_constructor_default_object_self_index_scan",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactComputedRatioClampReuse(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto find_relational_operator =
        [](std::string_view expression, std::size_t* op_pos, std::string* op) {
            if (op_pos == nullptr || op == nullptr) {
                return false;
            }
            static constexpr std::string_view kOps[] = {" <= ", " >= ", " < ", " > "};
            for (std::string_view token : kOps) {
                const std::size_t pos = FindBytecodeRepairTopLevelToken(expression, token);
                if (pos == std::string_view::npos) {
                    continue;
                }
                *op_pos = pos;
                *op = std::string(token);
                return true;
            }
            return false;
        };
    const auto split_top_level_division =
        [](std::string_view expression, std::string* lhs, std::string* rhs) {
            if (lhs == nullptr || rhs == nullptr) {
                return false;
            }
            expression = TrimWhitespace(expression);
            const std::size_t slash_pos =
                FindBytecodeRepairTopLevelToken(expression, " / ");
            if (slash_pos == std::string_view::npos) {
                return false;
            }
            *lhs = StripBytecodeRepairWholeParens(expression.substr(0, slash_pos));
            *rhs = StripBytecodeRepairWholeParens(expression.substr(slash_pos + 3));
            return !lhs->empty() && !rhs->empty();
        };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[line_index],
                                                 &is_local,
                                                 &lhs,
                                                 &rhs) ||
            !is_local || !refii::lua::IsValidLuaIdentifier(lhs)) {
            continue;
        }

        const std::string assigned_expression = StripBytecodeRepairWholeParens(rhs);
        std::string assigned_dividend;
        std::string assigned_divisor;
        if (assigned_expression.empty() ||
            assigned_expression.find(" / ") == std::string::npos ||
            assigned_expression.find(',') != std::string::npos ||
            !split_top_level_division(assigned_expression,
                                      &assigned_dividend,
                                      &assigned_divisor)) {
            continue;
        }

        std::unordered_map<std::string, std::string> prior_local_values;
        for (std::size_t prior_line = 0; prior_line < line_index; ++prior_line) {
            std::string prior_lhs;
            std::string prior_rhs;
            if (ParseBytecodeRepairLocalInitializer(lines[prior_line],
                                                    &prior_lhs,
                                                    &prior_rhs) &&
                refii::lua::IsValidLuaIdentifier(prior_lhs) &&
                prior_rhs.find(',') == std::string::npos) {
                prior_local_values[prior_lhs] =
                    StripBytecodeRepairWholeParens(prior_rhs);
            }
        }

        const auto equivalent_to_assigned_expression =
            [&](std::string_view expression) {
                const std::string stripped =
                    StripBytecodeRepairWholeParens(expression);
                if (stripped == assigned_expression) {
                    return true;
                }
                std::string candidate_dividend;
                std::string candidate_divisor;
                if (!split_top_level_division(stripped,
                                              &candidate_dividend,
                                              &candidate_divisor) ||
                    candidate_dividend != assigned_dividend) {
                    return false;
                }
                const auto assigned_divisor_value =
                    prior_local_values.find(assigned_divisor);
                if (assigned_divisor_value != prior_local_values.end() &&
                    assigned_divisor_value->second == candidate_divisor) {
                    return true;
                }
                const auto candidate_divisor_value =
                    prior_local_values.find(candidate_divisor);
                return candidate_divisor_value != prior_local_values.end() &&
                       candidate_divisor_value->second == assigned_divisor;
            };

        const std::size_t scan_limit =
            std::min<std::size_t>(lines.size(), line_index + 8);
        for (std::size_t if_line = line_index + 1; if_line < scan_limit; ++if_line) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[if_line]));
            if (!StartsWith(trimmed, "if ")) {
                continue;
            }
            const std::size_t then_pos =
                FindBytecodeRepairTopLevelToken(trimmed, " then");
            if (then_pos == std::string_view::npos) {
                continue;
            }

            const auto assigned_divisor_value =
                prior_local_values.find(assigned_divisor);
            if (assigned_divisor_value != prior_local_values.end()) {
                const std::string expanded_division =
                    assigned_dividend + " / " + assigned_divisor_value->second;
                std::string condition_text =
                    std::string(trimmed.substr(3, then_pos - 3));
                const std::size_t expanded_pos =
                    condition_text.find(expanded_division);
                if (expanded_pos != std::string::npos) {
                    condition_text.replace(expanded_pos,
                                           expanded_division.size(),
                                           lhs);
                    std::vector<std::string> transformed_lines = lines;
                    transformed_lines[if_line] =
                        GetLuaLineIndent(lines[if_line]) + "if " +
                        condition_text + " then";
                    if (TryCommitBytecodeParityExactCurrentFunctionLines(
                            context,
                            transformed_lines,
                            had_trailing_newline,
                            "bytecode_parity_candidate_computed_ratio_call_denominator_reuse",
                            candidate_source)) {
                        return true;
                    }
                    if (TryCommitBytecodeParityImprovingLines(
                            context,
                            transformed_lines,
                            had_trailing_newline,
                            "bytecode_parity_candidate_computed_ratio_call_denominator_reuse",
                            candidate_source)) {
                        return true;
                    }
                }
            }

            const std::string condition =
                StripBytecodeRepairWholeParens(trimmed.substr(3, then_pos - 3));
            std::size_t op_pos = std::string_view::npos;
            std::string op;
            if (!find_relational_operator(condition, &op_pos, &op)) {
                continue;
            }

            const std::string condition_lhs =
                StripBytecodeRepairWholeParens(condition.substr(0, op_pos));
            const std::string condition_rhs = StripBytecodeRepairWholeParens(
                condition.substr(op_pos + op.size()));

            std::string rebuilt_condition;
            if (equivalent_to_assigned_expression(condition_lhs)) {
                rebuilt_condition = lhs + op + condition_rhs;
            } else if (equivalent_to_assigned_expression(condition_rhs)) {
                rebuilt_condition = condition_lhs + op + lhs;
            } else {
                continue;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines[if_line] =
                GetLuaLineIndent(lines[if_line]) + "if " + rebuilt_condition + " then";
            if (TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_computed_ratio_clamp_reuse",
                    candidate_source)) {
                return true;
            }
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_computed_ratio_clamp_reuse",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactRepeatedSumDenominatorGrouping(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &lhs,
                                                &rhs) ||
            lhs.empty() || lhs.find(',') != std::string::npos) {
            continue;
        }

        const std::string expression = StripBytecodeRepairWholeParens(rhs);
        const std::size_t slash_pos =
            FindBytecodeRepairTopLevelToken(expression, " / ");
        if (slash_pos == std::string_view::npos) {
            continue;
        }
        const std::size_t plus_pos =
            FindBytecodeRepairTopLevelToken(
                std::string_view(expression).substr(slash_pos + 3), " + ");
        if (plus_pos == std::string_view::npos) {
            continue;
        }
        const std::size_t absolute_plus_pos = slash_pos + 3 + plus_pos;
        const std::string numerator =
            StripBytecodeRepairWholeParens(std::string_view(expression).substr(0, slash_pos));
        const std::string denominator = StripBytecodeRepairWholeParens(
            std::string_view(expression).substr(
                slash_pos + 3, absolute_plus_pos - (slash_pos + 3)));
        const std::string tail = StripBytecodeRepairWholeParens(
            std::string_view(expression).substr(absolute_plus_pos + 3));
        if (numerator.empty() || denominator.empty() || tail.empty() ||
            numerator != denominator || tail.find(',') != std::string::npos) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + (is_local ? "local " : "") +
            lhs + " = ((" + numerator + ") / (" + denominator + " + " + tail + "))";
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_repeated_sum_denominator_grouping",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_repeated_sum_denominator_grouping",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactEmptyLocalSelfSeed(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment(lines[line_index]));
        if (trimmed != "local self = {}") {
            continue;
        }

        const std::size_t next_line = next_nonempty_line(line_index + 1);
        if (next_line >= lines.size()) {
            continue;
        }
        const std::string_view next_trimmed =
            TrimWhitespace(StripLineComment(lines[next_line]));
        if (!StartsWith(next_trimmed, "self.") ||
            next_trimmed.find(" = ") == std::string_view::npos) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index));

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_empty_local_self_seed",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_empty_local_self_seed",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactActionCallTableLiteralHoist(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty() ||
            trimmed.find("Action.SetCurrentAction(") == std::string_view::npos) {
            continue;
        }
        const std::size_t table_open = stripped.find('{');
        if (table_open == std::string_view::npos) {
            continue;
        }
        const std::size_t table_close =
            FindBytecodeRepairMatchingBrace(stripped, table_open);
        if (table_close == std::string_view::npos || table_close <= table_open + 1) {
            continue;
        }
        const std::string_view table_literal =
            stripped.substr(table_open, table_close - table_open + 1);
        if (table_literal.find('=') == std::string_view::npos ||
            table_literal == "{}") {
            continue;
        }

        std::string temp_name = "refii_action_call_tmp_0";
        {
            std::size_t suffix = 0;
            auto temp_in_use = [&](std::string_view candidate) {
                for (const std::string& line : lines) {
                    if (line.find(candidate) != std::string::npos) {
                        return true;
                    }
                }
                return false;
            };
            while (temp_in_use(temp_name)) {
                ++suffix;
                temp_name = "refii_action_call_tmp_" + std::to_string(suffix);
            }
        }

        const std::string rewritten_line =
            std::string(stripped.substr(0, table_open)) + temp_name +
            std::string(stripped.substr(table_close + 1));
        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] = rewritten_line;
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index),
            GetLuaLineIndent(lines[line_index]) + "local " + temp_name + " = " +
                std::string(table_literal));
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_near_exact_action_call_table_hoist",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactDuplicateBreakGuardComparison(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 6) {
        return false;
    }

    struct ParsedComparison {
        std::string lhs;
        std::string op;
        std::string rhs;
    };

    const auto parse_top_level_comparison =
        [](std::string_view expression, ParsedComparison* parsed_out) {
            if (parsed_out == nullptr) {
                return false;
            }
            *parsed_out = ParsedComparison{};

            const std::string_view trimmed = TrimWhitespace(expression);
            if (trimmed.empty()) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t op_pos = std::string_view::npos;
            std::string op;

            for (std::size_t i = 0; i < trimmed.size(); ++i) {
                const char ch = trimmed[i];
                if (in_single_quote) {
                    if (ch == '\\' && i + 1 < trimmed.size()) {
                        ++i;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && i + 1 < trimmed.size()) {
                        ++i;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    paren_depth = std::max(0, paren_depth - 1);
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    brace_depth = std::max(0, brace_depth - 1);
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    bracket_depth = std::max(0, bracket_depth - 1);
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }

                if (i + 1 < trimmed.size()) {
                    const std::string_view two = trimmed.substr(i, 2);
                    if (two == "==" || two == "~=" || two == "<=" || two == ">=") {
                        op_pos = i;
                        op = std::string(two);
                        break;
                    }
                }
                if (ch == '<' || ch == '>') {
                    op_pos = i;
                    op.assign(1, ch);
                    break;
                }
            }

            if (op_pos == std::string_view::npos || op.empty() ||
                op_pos + op.size() >= trimmed.size()) {
                return false;
            }

            const std::string lhs =
                std::string(TrimWhitespace(trimmed.substr(0, op_pos)));
            const std::string rhs =
                std::string(TrimWhitespace(trimmed.substr(op_pos + op.size())));
            if (lhs.empty() || rhs.empty()) {
                return false;
            }

            parsed_out->lhs = lhs;
            parsed_out->op = std::move(op);
            parsed_out->rhs = rhs;
            return true;
        };

    const auto are_equivalent_comparisons =
        [](const ParsedComparison& first, const ParsedComparison& second) {
            if (first.lhs == second.lhs &&
                first.op == second.op &&
                first.rhs == second.rhs) {
                return true;
            }

            const auto mirror_op = [](std::string_view op) -> std::string {
                if (op == "<") {
                    return ">";
                }
                if (op == ">") {
                    return "<";
                }
                if (op == "<=") {
                    return ">=";
                }
                if (op == ">=") {
                    return "<=";
                }
                if (op == "==") {
                    return "==";
                }
                if (op == "~=") {
                    return "~=";
                }
                return std::string();
            };

            return first.lhs == second.rhs &&
                   first.rhs == second.lhs &&
                   mirror_op(first.op) == second.op;
        };

    struct IfBreakBlock {
        std::size_t line_start = 0;
        std::size_t line_break = 0;
        std::size_t line_end = 0;
        ParsedComparison comparison;
        bool valid = false;
    };

    const auto parse_if_break_block =
        [&](std::size_t start_line) {
            IfBreakBlock block;
            block.line_start = start_line;
            if (start_line >= lines.size()) {
                return block;
            }

            const std::string_view start_trimmed =
                TrimWhitespace(StripLineComment(lines[start_line]));
            if (!StartsWith(start_trimmed, "if ") ||
                !start_trimmed.ends_with(" then")) {
                return block;
            }

            const std::string_view condition_text = TrimWhitespace(
                start_trimmed.substr(3, start_trimmed.size() - 3 - 5));
            if (!parse_top_level_comparison(condition_text, &block.comparison)) {
                return block;
            }

            std::size_t break_line = start_line + 1;
            while (break_line < lines.size() &&
                   TrimWhitespace(StripLineComment(lines[break_line])).empty()) {
                ++break_line;
            }
            if (break_line >= lines.size() ||
                TrimWhitespace(StripLineComment(lines[break_line])) != "break") {
                return block;
            }

            std::size_t end_line = break_line + 1;
            while (end_line < lines.size() &&
                   TrimWhitespace(StripLineComment(lines[end_line])).empty()) {
                ++end_line;
            }
            if (end_line >= lines.size() ||
                TrimWhitespace(StripLineComment(lines[end_line])) != "end") {
                return block;
            }

            block.line_break = break_line;
            block.line_end = end_line;
            block.valid = true;
            return block;
        };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const IfBreakBlock first_block = parse_if_break_block(line_index);
        if (!first_block.valid) {
            continue;
        }

        std::size_t second_start = first_block.line_end + 1;
        while (second_start < lines.size() &&
               TrimWhitespace(StripLineComment(lines[second_start])).empty()) {
            ++second_start;
        }
        const IfBreakBlock second_block = parse_if_break_block(second_start);
        if (!second_block.valid) {
            continue;
        }

        if (!are_equivalent_comparisons(first_block.comparison, second_block.comparison)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(second_block.line_start),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(second_block.line_end + 1));

        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_near_exact_duplicate_break_guard",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactDuplicateGroupMethodCallChain(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto mismatch_delta_sum =
        [](const refii::lua::audit::FunctionMismatchInfo& mismatch) {
            if (!mismatch.available) {
                return 0LL;
            }
            return std::llabs(mismatch.instruction_delta) +
                   std::llabs(mismatch.constant_delta) +
                   std::llabs(mismatch.local_delta);
        };

    const auto try_commit = [&](const std::vector<std::string>& transformed_lines,
                                std::string_view compile_label) {
        const std::string transformed_source =
            JoinLines(transformed_lines, had_trailing_newline);
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(transformed_source,
                                           std::string(compile_label).c_str(),
                                           &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            return false;
        }

        const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
            refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                         transformed_chunk.chunk);
        if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch)) {
            return false;
        }
        const bool strict_mismatch_improvement =
            !transformed_mismatch.available ||
            transformed_mismatch.function_index > context.mismatch.function_index ||
            (transformed_mismatch.function_index == context.mismatch.function_index &&
             mismatch_delta_sum(transformed_mismatch) <
                 mismatch_delta_sum(context.mismatch));
        if (!strict_mismatch_improvement) {
            return false;
        }

        *candidate_source = transformed_source;
        return true;
    };

    const auto parse_identifier = [](std::string_view text, std::size_t start) {
        std::size_t end = start;
        while (end < text.size() &&
               (std::isalnum(static_cast<unsigned char>(text[end])) || text[end] == '_')) {
            ++end;
        }
        return end;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string& line = lines[line_index];
        std::size_t cursor = 0;
        bool line_changed = false;
        std::string rewritten = line;
        while (cursor < rewritten.size()) {
            const std::size_t colon = rewritten.find(':', cursor);
            if (colon == std::string::npos || colon + 1 >= rewritten.size()) {
                break;
            }
            const std::size_t method1_end = parse_identifier(rewritten, colon + 1);
            if (method1_end <= colon + 1 ||
                method1_end + 2 > rewritten.size() ||
                rewritten.compare(method1_end, 2, "()") != 0) {
                cursor = colon + 1;
                continue;
            }
            const std::size_t group_pos = method1_end + 2;
            if (group_pos + 7 >= rewritten.size() ||
                rewritten.compare(group_pos, 7, ".Group:") != 0) {
                cursor = colon + 1;
                continue;
            }
            const std::size_t method2_start = group_pos + 7;
            const std::size_t method2_end = parse_identifier(rewritten, method2_start);
            if (method2_end <= method2_start ||
                method2_end + 2 > rewritten.size() ||
                rewritten.compare(method2_end, 2, "()") != 0) {
                cursor = colon + 1;
                continue;
            }

            const std::string method1 = rewritten.substr(colon + 1, method1_end - (colon + 1));
            const std::string method2 =
                rewritten.substr(method2_start, method2_end - method2_start);
            if (method1 != method2) {
                cursor = colon + 1;
                continue;
            }

            rewritten.replace(colon,
                             method2_end + 2 - colon,
                             ":" + method1 + "()");
            line_changed = true;
            cursor = colon + method1.size() + 3;
        }

        if (!line_changed || rewritten == line) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] = std::move(rewritten);
        if (try_commit(transformed_lines,
                       "bytecode_parity_candidate_near_exact_group_method_chain")) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactAssertIfElseBoolean(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 3 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto mismatch_delta_sum =
        [](const refii::lua::audit::FunctionMismatchInfo& mismatch) {
            if (!mismatch.available) {
                return 0LL;
            }
            return std::llabs(mismatch.instruction_delta) +
                   std::llabs(mismatch.constant_delta) +
                   std::llabs(mismatch.local_delta);
        };

    const auto try_commit = [&](const std::vector<std::string>& transformed_lines,
                                std::string_view compile_label) {
        const std::string transformed_source =
            JoinLines(transformed_lines, had_trailing_newline);
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(transformed_source,
                                           std::string(compile_label).c_str(),
                                           &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            return false;
        }

        const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
            refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                         transformed_chunk.chunk);
        if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch)) {
            return false;
        }
        const bool strict_mismatch_improvement =
            !transformed_mismatch.available ||
            transformed_mismatch.function_index > context.mismatch.function_index ||
            (transformed_mismatch.function_index == context.mismatch.function_index &&
             mismatch_delta_sum(transformed_mismatch) <
                 mismatch_delta_sum(context.mismatch));
        if (!strict_mismatch_improvement) {
            return false;
        }

        *candidate_source = transformed_source;
        return true;
    };

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    const auto parse_assert_boolean = [](std::string_view line,
                                         bool* value_out) {
        if (value_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (!StartsWith(trimmed, "assert(") || !trimmed.ends_with(")")) {
            return false;
        }
        std::string_view inner = TrimWhitespace(trimmed.substr(7, trimmed.size() - 8));
        while (inner.size() >= 2 && inner.front() == '(' && inner.back() == ')') {
            inner = TrimWhitespace(inner.substr(1, inner.size() - 2));
        }
        if (inner == "true") {
            *value_out = true;
            return true;
        }
        if (inner == "false") {
            *value_out = false;
            return true;
        }
        return false;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view if_trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::string condition =
            std::string(TrimWhitespace(if_trimmed.substr(3, if_trimmed.size() - 8)));
        if (condition.empty()) {
            continue;
        }

        const std::size_t first_assert_line = next_nonempty_line(line_index + 1);
        const std::size_t else_line = next_nonempty_line(first_assert_line + 1);
        const std::size_t second_assert_line = next_nonempty_line(else_line + 1);
        const std::size_t end_line = next_nonempty_line(second_assert_line + 1);
        if (first_assert_line >= lines.size() ||
            else_line >= lines.size() ||
            second_assert_line >= lines.size() ||
            end_line >= lines.size()) {
            continue;
        }
        if (TrimWhitespace(StripLineComment(lines[else_line])) != "else" ||
            TrimWhitespace(StripLineComment(lines[end_line])) != "end") {
            continue;
        }

        bool first_assert_value = false;
        bool second_assert_value = false;
        if (!parse_assert_boolean(lines[first_assert_line], &first_assert_value) ||
            !parse_assert_boolean(lines[second_assert_line], &second_assert_value) ||
            first_assert_value == second_assert_value) {
            continue;
        }

        const bool assert_condition = first_assert_value && !second_assert_value;
        const std::string indent = GetLuaLineIndent(lines[line_index]);
        const std::string rewritten_line =
            assert_condition
                ? (indent + "assert(" + condition + ")")
                : (indent + "assert(not (" + condition + "))");

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(end_line + 1));
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index),
            rewritten_line);
        if (try_commit(transformed_lines,
                       "bytecode_parity_candidate_near_exact_assert_if_else_boolean")) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactAssertNilPolarity(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("instruction_diff") == std::string::npos ||
        context.mismatch.note.find("EQ(") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty() || !NormalizeRenderedLuaSingleAssertNilPolarityForParity(&lines)) {
        return false;
    }

    const std::string transformed_source = JoinLines(lines, had_trailing_newline);
    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(transformed_source,
                                       "bytecode_parity_candidate_near_exact_assert_nil_polarity",
                                       &transformed_chunk) ||
        !transformed_chunk.chunk_parsed) {
        return false;
    }

    const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
        refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                     transformed_chunk.chunk);
    if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch)) {
        return false;
    }

    const auto mismatch_delta_sum =
        [](const refii::lua::audit::FunctionMismatchInfo& mismatch) {
            if (!mismatch.available) {
                return 0LL;
            }
            return std::llabs(mismatch.instruction_delta) +
                   std::llabs(mismatch.constant_delta) +
                   std::llabs(mismatch.local_delta);
        };
    const bool strict_mismatch_improvement =
        !transformed_mismatch.available ||
        transformed_mismatch.function_index > context.mismatch.function_index ||
        (transformed_mismatch.function_index == context.mismatch.function_index &&
         mismatch_delta_sum(transformed_mismatch) < mismatch_delta_sum(context.mismatch));
    if (!strict_mismatch_improvement) {
        return false;
    }

    *candidate_source = transformed_source;
    return true;
}

bool TryRepairNearExactReferenceCarrySingletonPatchFallback(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    const auto mismatch_delta_sum =
        [](const refii::lua::audit::FunctionMismatchInfo& mismatch) {
            if (!mismatch.available) {
                return 0LL;
            }
            return std::llabs(mismatch.instruction_delta) +
                   std::llabs(mismatch.constant_delta) +
                   std::llabs(mismatch.local_delta);
        };

    const auto parse_simple_assignment = [](std::string_view line,
                                            std::string* lhs_out,
                                            std::string* rhs_out) {
        if (lhs_out == nullptr || rhs_out == nullptr) {
            return false;
        }
        *lhs_out = std::string();
        *rhs_out = std::string();
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (trimmed.empty() || StartsWith(trimmed, "local ")) {
            return false;
        }
        const std::size_t eq = trimmed.find('=');
        if (eq == std::string_view::npos || eq == 0 || eq + 1 >= trimmed.size() ||
            trimmed.find("==") != std::string_view::npos) {
            return false;
        }
        *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, eq)));
        *rhs_out = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
        return !lhs_out->empty() && !rhs_out->empty();
    };

    const auto parse_singleton_table_literal =
        [](std::string_view rhs,
           std::string* key_out,
           std::string* value_out,
           bool* key_is_identifier_out) {
            if (key_out == nullptr || value_out == nullptr ||
                key_is_identifier_out == nullptr) {
                return false;
            }
            const std::string_view trimmed = TrimWhitespace(rhs);
            if (trimmed.size() < 3 || trimmed.front() != '{' || trimmed.back() != '}') {
                return false;
            }
            const std::string_view body =
                TrimWhitespace(trimmed.substr(1, trimmed.size() - 2));
            if (body.empty() || body.find(',') != std::string_view::npos) {
                return false;
            }
            const std::size_t equals_pos = body.find('=');
            if (equals_pos == std::string_view::npos ||
                equals_pos == 0 ||
                equals_pos + 1 >= body.size()) {
                return false;
            }
            const std::string_view key =
                TrimWhitespace(body.substr(0, equals_pos));
            const std::string_view value =
                TrimWhitespace(body.substr(equals_pos + 1));
            if (key.empty() || value.empty()) {
                return false;
            }

            bool key_is_identifier = false;
            if (refii::lua::IsValidLuaIdentifier(key)) {
                key_is_identifier = true;
            } else if (key.size() >= 3 && key.front() == '[' && key.back() == ']') {
                const std::string_view inner =
                    TrimWhitespace(key.substr(1, key.size() - 2));
                if (inner.empty()) {
                    return false;
                }
            } else {
                return false;
            }

            *key_out = std::string(key);
            *value_out = std::string(value);
            *key_is_identifier_out = key_is_identifier;
            return true;
        };

    const auto is_reference_carry_rhs = [](std::string_view rhs_expression) {
        const std::string_view rhs = TrimWhitespace(rhs_expression);
        if (rhs.empty() || rhs == "{}" || rhs == "nil" || rhs == "true" || rhs == "false") {
            return false;
        }
        if (rhs.front() == '"' || rhs.front() == '\'' ||
            std::isdigit(static_cast<unsigned char>(rhs.front())) != 0 ||
            rhs.front() == '{' || rhs.front() == '(') {
            return false;
        }
        if (rhs.find(':') != std::string_view::npos ||
            rhs.find('(') != std::string_view::npos ||
            rhs.find(')') != std::string_view::npos ||
            rhs.find('{') != std::string_view::npos ||
            rhs.find('}') != std::string_view::npos ||
            rhs.find(',') != std::string_view::npos) {
            return false;
        }
        return true;
    };

    const auto try_commit = [&](const std::vector<std::string>& transformed_lines,
                                std::string_view compile_label) {
        const std::string transformed_source =
            JoinLines(transformed_lines, had_trailing_newline);
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(transformed_source,
                                           std::string(compile_label).c_str(),
                                           &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            return false;
        }

        const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
            refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                         transformed_chunk.chunk);
        if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch)) {
            return false;
        }
        const bool strict_improvement =
            !transformed_mismatch.available ||
            transformed_mismatch.function_index > context.mismatch.function_index ||
            (transformed_mismatch.function_index == context.mismatch.function_index &&
             mismatch_delta_sum(transformed_mismatch) <
                 mismatch_delta_sum(context.mismatch));
        if (!strict_improvement) {
            return false;
        }

        *candidate_source = transformed_source;
        return true;
    };

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        std::string first_lhs;
        std::string first_rhs;
        if (!parse_simple_assignment(lines[line_index], &first_lhs, &first_rhs) ||
            !is_reference_carry_rhs(first_rhs)) {
            continue;
        }

        std::size_t second_line = line_index + 1;
        while (second_line < lines.size() &&
               TrimWhitespace(StripLineComment(lines[second_line])).empty()) {
            ++second_line;
        }
        if (second_line >= lines.size()) {
            continue;
        }

        std::string second_lhs;
        std::string second_rhs;
        if (!parse_simple_assignment(lines[second_line], &second_lhs, &second_rhs) ||
            second_lhs != first_lhs) {
            continue;
        }

        std::string table_key;
        std::string table_value;
        bool key_is_identifier = false;
        if (!parse_singleton_table_literal(second_rhs,
                                           &table_key,
                                           &table_value,
                                           &key_is_identifier)) {
            continue;
        }

        std::string patched_lhs = first_lhs;
        if (key_is_identifier) {
            patched_lhs += "." + table_key;
        } else {
            patched_lhs += table_key;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[second_line] =
            GetLuaLineIndent(lines[second_line]) + patched_lhs + " = " + table_value;
        if (try_commit(transformed_lines,
                       "bytecode_parity_candidate_near_exact_refcarry_singleton_patch")) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactSingletonTableAssignmentToFieldPatch(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto parse_simple_assignment =
        [](std::string_view line, std::string* lhs_out, std::string* rhs_out) {
            if (lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *lhs_out = std::string();
            *rhs_out = std::string();
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (trimmed.empty() || StartsWith(trimmed, "local ")) {
                return false;
            }
            const std::size_t eq = trimmed.find('=');
            if (eq == std::string_view::npos || eq == 0 || eq + 1 >= trimmed.size() ||
                trimmed.find("==") != std::string_view::npos ||
                trimmed.find("~=") != std::string_view::npos ||
                trimmed.find("<=") != std::string_view::npos ||
                trimmed.find(">=") != std::string_view::npos) {
                return false;
            }
            *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, eq)));
            *rhs_out = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto parse_singleton_table_literal =
        [](std::string_view rhs,
           std::string* key_out,
           std::string* value_out,
           bool* key_is_identifier_out) {
            if (key_out == nullptr || value_out == nullptr ||
                key_is_identifier_out == nullptr) {
                return false;
            }
            const std::string_view trimmed = TrimWhitespace(rhs);
            if (trimmed.size() < 3 || trimmed.front() != '{' || trimmed.back() != '}') {
                return false;
            }
            const std::string_view body =
                TrimWhitespace(trimmed.substr(1, trimmed.size() - 2));
            if (body.empty() || body.find(',') != std::string_view::npos) {
                return false;
            }
            const std::size_t equals_pos = body.find('=');
            if (equals_pos == std::string_view::npos ||
                equals_pos == 0 ||
                equals_pos + 1 >= body.size()) {
                return false;
            }
            const std::string_view key = TrimWhitespace(body.substr(0, equals_pos));
            const std::string_view value = TrimWhitespace(body.substr(equals_pos + 1));
            if (key.empty() || value.empty()) {
                return false;
            }

            bool key_is_identifier = false;
            if (refii::lua::IsValidLuaIdentifier(key)) {
                key_is_identifier = true;
            } else if (key.size() >= 3 && key.front() == '[' && key.back() == ']') {
                const std::string_view inner =
                    TrimWhitespace(key.substr(1, key.size() - 2));
                if (inner.empty()) {
                    return false;
                }
            } else {
                return false;
            }

            *key_out = std::string(key);
            *value_out = std::string(value);
            *key_is_identifier_out = key_is_identifier;
            return true;
        };

    const auto lhs_is_existing_table_path = [](std::string_view lhs) {
        lhs = TrimWhitespace(lhs);
        if (lhs.empty() || refii::lua::IsRenderTemporaryName(lhs) ||
            lhs.find(',') != std::string_view::npos ||
            lhs.find(':') != std::string_view::npos ||
            lhs.find('(') != std::string_view::npos ||
            lhs.find(')') != std::string_view::npos) {
            return false;
        }
        return lhs.find('.') != std::string_view::npos ||
               lhs.find('[') != std::string_view::npos;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string lhs;
        std::string rhs;
        if (!parse_simple_assignment(lines[line_index], &lhs, &rhs) ||
            !lhs_is_existing_table_path(lhs)) {
            continue;
        }

        std::string table_key;
        std::string table_value;
        bool key_is_identifier = false;
        if (!parse_singleton_table_literal(rhs,
                                           &table_key,
                                           &table_value,
                                           &key_is_identifier)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + lhs +
            (key_is_identifier ? "." + table_key : table_key) + " = " + table_value;
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_singleton_table_assignment_to_field",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactAliasConstructorToFieldPatch(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto parse_simple_assignment =
        [](std::string_view line, std::string* lhs_out, std::string* rhs_out) {
            if (lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *lhs_out = std::string();
            *rhs_out = std::string();
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (trimmed.empty() || StartsWith(trimmed, "local ")) {
                return false;
            }
            const std::size_t eq = trimmed.find('=');
            if (eq == std::string_view::npos || eq == 0 || eq + 1 >= trimmed.size() ||
                trimmed.find("==") != std::string_view::npos ||
                trimmed.find("~=") != std::string_view::npos ||
                trimmed.find("<=") != std::string_view::npos ||
                trimmed.find(">=") != std::string_view::npos) {
                return false;
            }
            *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, eq)));
            *rhs_out = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto is_safe_table_path = [](std::string_view expression) {
        expression = TrimWhitespace(expression);
        if (expression.empty() || refii::lua::IsRenderTemporaryName(expression) ||
            expression.find(',') != std::string_view::npos ||
            expression.find(':') != std::string_view::npos ||
            expression.find('(') != std::string_view::npos ||
            expression.find(')') != std::string_view::npos ||
            expression.find('[') != std::string_view::npos ||
            expression.find(']') != std::string_view::npos) {
            return false;
        }
        std::size_t token_start = 0;
        while (token_start < expression.size()) {
            const std::size_t dot = expression.find('.', token_start);
            const std::string_view token =
                dot == std::string_view::npos
                    ? expression.substr(token_start)
                    : expression.substr(token_start, dot - token_start);
            if (!refii::lua::IsValidLuaIdentifier(TrimWhitespace(token))) {
                return false;
            }
            if (dot == std::string_view::npos) {
                break;
            }
            token_start = dot + 1;
        }
        return true;
    };

    const auto split_top_level_entries =
        [](std::string_view body) -> std::vector<std::string> {
            std::vector<std::string> entries;
            std::size_t entry_start = 0;
            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            for (std::size_t index = 0; index <= body.size(); ++index) {
                const char ch = index < body.size() ? body[index] : ',';
                if (index < body.size()) {
                    if (ch == '\'' && !in_double_quote) {
                        in_single_quote = !in_single_quote;
                        continue;
                    }
                    if (ch == '"' && !in_single_quote) {
                        in_double_quote = !in_double_quote;
                        continue;
                    }
                    if (in_single_quote || in_double_quote) {
                        continue;
                    }
                    if (ch == '(') {
                        ++paren_depth;
                        continue;
                    }
                    if (ch == ')' && paren_depth > 0) {
                        --paren_depth;
                        continue;
                    }
                    if (ch == '{') {
                        ++brace_depth;
                        continue;
                    }
                    if (ch == '}' && brace_depth > 0) {
                        --brace_depth;
                        continue;
                    }
                    if (ch == '[') {
                        ++bracket_depth;
                        continue;
                    }
                    if (ch == ']' && bracket_depth > 0) {
                        --bracket_depth;
                        continue;
                    }
                }
                if (ch != ',' || in_single_quote || in_double_quote ||
                    paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                const std::string_view entry =
                    TrimWhitespace(body.substr(entry_start, index - entry_start));
                if (!entry.empty()) {
                    entries.emplace_back(entry);
                }
                entry_start = index + 1;
            }
            return entries;
        };

    const auto parse_key_value =
        [](std::string_view entry, std::string* key_out, std::string* value_out) {
            if (key_out == nullptr || value_out == nullptr) {
                return false;
            }
            entry = TrimWhitespace(entry);
            const std::size_t equals_pos = entry.find('=');
            if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                equals_pos + 1 >= entry.size()) {
                return false;
            }
            const std::string_view key = TrimWhitespace(entry.substr(0, equals_pos));
            const std::string_view value = TrimWhitespace(entry.substr(equals_pos + 1));
            if (!refii::lua::IsValidLuaIdentifier(key) || value.empty()) {
                return false;
            }
            *key_out = std::string(key);
            *value_out = std::string(value);
            return true;
        };

    const auto parse_singleton_table_literal =
        [&](std::string_view rhs,
            std::string* key_out,
            std::string* value_out,
            bool* key_is_identifier_out) {
            if (key_out == nullptr || value_out == nullptr ||
                key_is_identifier_out == nullptr) {
                return false;
            }
            rhs = TrimWhitespace(rhs);
            if (rhs.size() < 3 || rhs.front() != '{' || rhs.back() != '}') {
                return false;
            }
            const std::string_view body = TrimWhitespace(rhs.substr(1, rhs.size() - 2));
            const std::vector<std::string> entries = split_top_level_entries(body);
            if (entries.size() != 1) {
                return false;
            }

            std::string key;
            std::string value;
            if (!parse_key_value(entries.front(), &key, &value)) {
                return false;
            }
            *key_out = std::move(key);
            *value_out = std::move(value);
            *key_is_identifier_out = true;
            return true;
        };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string lhs;
        std::string rhs;
        if (!parse_simple_assignment(lines[line_index], &lhs, &rhs) ||
            !is_safe_table_path(lhs)) {
            continue;
        }

        const std::string_view rhs_trimmed = TrimWhitespace(rhs);
        if (rhs_trimmed.size() < 5 || rhs_trimmed.front() != '{' ||
            rhs_trimmed.back() != '}') {
            continue;
        }

        const std::vector<std::string> outer_entries = split_top_level_entries(
            TrimWhitespace(rhs_trimmed.substr(1, rhs_trimmed.size() - 2)));
        if (outer_entries.size() != 2) {
            continue;
        }

        std::string first_key;
        std::string first_value;
        std::string second_key;
        std::string second_value;
        if (!parse_key_value(outer_entries[0], &first_key, &first_value) ||
            !parse_key_value(outer_entries[1], &second_key, &second_value) ||
            first_key != second_key) {
            continue;
        }

        const std::string alias_value = lhs + "." + first_key;
        std::string nested_literal =
            TrimWhitespace(first_value) == alias_value ? second_value : first_value;
        const std::string alias_candidate =
            TrimWhitespace(first_value) == alias_value ? first_value : second_value;
        if (TrimWhitespace(alias_candidate) != alias_value) {
            continue;
        }

        std::string nested_key;
        std::string nested_value;
        bool nested_key_is_identifier = false;
        if (!parse_singleton_table_literal(nested_literal,
                                           &nested_key,
                                           &nested_value,
                                           &nested_key_is_identifier)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + lhs + "." + first_key +
            (nested_key_is_identifier ? "." + nested_key : nested_key) + " = " +
            nested_value;
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_alias_constructor_to_field",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactRedundantSelfAssignment(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 64 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto parse_simple_assignment =
        [](std::string_view line, std::string* lhs_out, std::string* rhs_out) {
            if (lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *lhs_out = std::string();
            *rhs_out = std::string();
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (trimmed.empty() || StartsWith(trimmed, "local ")) {
                return false;
            }
            const std::size_t eq = trimmed.find('=');
            if (eq == std::string_view::npos || eq == 0 || eq + 1 >= trimmed.size() ||
                trimmed.find("==") != std::string_view::npos ||
                trimmed.find("~=") != std::string_view::npos ||
                trimmed.find("<=") != std::string_view::npos ||
                trimmed.find(">=") != std::string_view::npos) {
                return false;
            }
            *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, eq)));
            *rhs_out = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto is_safe_table_path = [](std::string_view expression) {
        expression = TrimWhitespace(expression);
        return !expression.empty() &&
               !refii::lua::IsRenderTemporaryName(expression) &&
               expression.find('.') != std::string_view::npos &&
               expression.find(',') == std::string_view::npos &&
               expression.find(':') == std::string_view::npos &&
               expression.find('(') == std::string_view::npos &&
               expression.find(')') == std::string_view::npos &&
               expression.find('[') == std::string_view::npos &&
               expression.find(']') == std::string_view::npos;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string lhs;
        std::string rhs;
        if (!parse_simple_assignment(lines[line_index], &lhs, &rhs) ||
            lhs != rhs || !is_safe_table_path(lhs)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(line_index));
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_redundant_self_assignment",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactTablePathIdentifierSeedToEmptyTable(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto parse_simple_assignment =
        [](std::string_view line, std::string* lhs_out, std::string* rhs_out) {
            if (lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *lhs_out = std::string();
            *rhs_out = std::string();
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (trimmed.empty() || StartsWith(trimmed, "local ")) {
                return false;
            }
            const std::size_t eq = trimmed.find('=');
            if (eq == std::string_view::npos || eq == 0 || eq + 1 >= trimmed.size() ||
                trimmed.find("==") != std::string_view::npos ||
                trimmed.find("~=") != std::string_view::npos ||
                trimmed.find("<=") != std::string_view::npos ||
                trimmed.find(">=") != std::string_view::npos) {
                return false;
            }
            *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, eq)));
            *rhs_out = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto lhs_is_table_path = [](std::string_view lhs) {
        lhs = TrimWhitespace(lhs);
        return !lhs.empty() &&
               !refii::lua::IsRenderTemporaryName(lhs) &&
               lhs.find(',') == std::string_view::npos &&
               lhs.find(':') == std::string_view::npos &&
               lhs.find('(') == std::string_view::npos &&
               lhs.find(')') == std::string_view::npos &&
               (lhs.find('.') != std::string_view::npos ||
                lhs.find('[') != std::string_view::npos);
    };

    const auto rhs_is_identifier_path = [](std::string_view rhs) {
        rhs = TrimWhitespace(rhs);
        if (rhs.empty() || rhs == "nil" || rhs == "true" || rhs == "false" ||
            rhs.find(',') != std::string_view::npos ||
            rhs.find(':') != std::string_view::npos ||
            rhs.find('(') != std::string_view::npos ||
            rhs.find(')') != std::string_view::npos ||
            rhs.find('[') != std::string_view::npos ||
            rhs.find(']') != std::string_view::npos ||
            rhs.find('{') != std::string_view::npos ||
            rhs.find('}') != std::string_view::npos) {
            return false;
        }
        std::size_t token_start = 0;
        while (token_start < rhs.size()) {
            const std::size_t dot = rhs.find('.', token_start);
            const std::string_view token =
                dot == std::string_view::npos
                    ? rhs.substr(token_start)
                    : rhs.substr(token_start, dot - token_start);
            if (!refii::lua::IsValidLuaIdentifier(TrimWhitespace(token))) {
                return false;
            }
            if (dot == std::string_view::npos) {
                break;
            }
            token_start = dot + 1;
        }
        return true;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string lhs;
        std::string rhs;
        if (!parse_simple_assignment(lines[line_index], &lhs, &rhs) ||
            !lhs_is_table_path(lhs) || !rhs_is_identifier_path(rhs)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + lhs + " = {}";
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_table_path_identifier_seed_empty_table",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactRedundantTablePathSeedAssignment(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 3 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    const auto is_table_path_lhs = [](std::string_view lhs) {
        lhs = TrimWhitespace(lhs);
        return !lhs.empty() &&
               !refii::lua::IsRenderTemporaryName(lhs) &&
               lhs.find(',') == std::string_view::npos &&
               lhs.find(':') == std::string_view::npos &&
               lhs.find('(') == std::string_view::npos &&
               lhs.find(')') == std::string_view::npos &&
               (lhs.find('.') != std::string_view::npos ||
                lhs.find('[') != std::string_view::npos);
    };

    const auto is_seed_rhs = [](std::string_view rhs) {
        rhs = TrimWhitespace(rhs);
        if (rhs == "{}") {
            return true;
        }
        if (rhs.empty() || rhs == "nil" || rhs == "true" || rhs == "false" ||
            rhs.find(',') != std::string_view::npos ||
            rhs.find(':') != std::string_view::npos ||
            rhs.find('(') != std::string_view::npos ||
            rhs.find(')') != std::string_view::npos ||
            rhs.find('[') != std::string_view::npos ||
            rhs.find(']') != std::string_view::npos ||
            rhs.find('{') != std::string_view::npos ||
            rhs.find('}') != std::string_view::npos) {
            return false;
        }
        std::size_t token_start = 0;
        while (token_start < rhs.size()) {
            const std::size_t dot = rhs.find('.', token_start);
            const std::string_view token =
                dot == std::string_view::npos
                    ? rhs.substr(token_start)
                    : rhs.substr(token_start, dot - token_start);
            if (!refii::lua::IsValidLuaIdentifier(TrimWhitespace(token))) {
                return false;
            }
            if (dot == std::string_view::npos) {
                break;
            }
            token_start = dot + 1;
        }
        return true;
    };

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &lhs,
                                                &rhs) ||
            is_local || !is_table_path_lhs(lhs) || !is_seed_rhs(rhs)) {
            continue;
        }

        std::size_t next_index = line_index + 1;
        while (next_index < lines.size() &&
               TrimWhitespace(StripLineComment(lines[next_index])).empty()) {
            ++next_index;
        }
        if (next_index >= lines.size()) {
            continue;
        }

        bool next_is_local = false;
        std::string next_lhs;
        std::string next_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[next_index],
                                                &next_is_local,
                                                &next_lhs,
                                                &next_rhs) ||
            next_is_local) {
            continue;
        }
        const std::string field_prefix = lhs + ".";
        if (!StartsWith(TrimWhitespace(next_lhs), field_prefix)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(line_index));
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_redundant_table_path_seed_assignment",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_redundant_table_path_seed_assignment",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactDuplicateCallReassignment(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &lhs,
                                                &rhs) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(lhs) ||
            refii::lua::IsRenderTemporaryName(lhs) ||
            rhs.find('(') == std::string::npos ||
            rhs.find(')') == std::string::npos) {
            continue;
        }

        const std::size_t end_index = std::min(lines.size(), line_index + 7);
        for (std::size_t duplicate_index = line_index + 1;
             duplicate_index < end_index;
             ++duplicate_index) {
            const std::string_view duplicate_trimmed =
                TrimWhitespace(StripLineComment(lines[duplicate_index]));
            if (duplicate_trimmed.empty()) {
                continue;
            }

            bool duplicate_is_local = false;
            std::string duplicate_lhs;
            std::string duplicate_rhs;
            if (!ParseBytecodeRepairPlainAssignment(lines[duplicate_index],
                                                    &duplicate_is_local,
                                                    &duplicate_lhs,
                                                    &duplicate_rhs)) {
                break;
            }
            if (duplicate_lhs == lhs && duplicate_rhs != rhs) {
                break;
            }
            if (duplicate_is_local || duplicate_lhs != lhs || duplicate_rhs != rhs) {
                continue;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines.erase(transformed_lines.begin() +
                                    static_cast<std::ptrdiff_t>(duplicate_index));
            if (TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_duplicate_call_reassignment",
                    candidate_source)) {
                return true;
            }
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_duplicate_call_reassignment",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactReturnedSelfSeedFieldTableLiteral(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 6 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 3 || lines.size() > 12000) {
        return false;
    }

    const auto parse_simple_field_lhs =
        [](std::string_view lhs,
           std::string_view table_name,
           std::string* field_out) {
            if (field_out == nullptr || table_name.empty()) {
                return false;
            }
            lhs = TrimWhitespace(lhs);
            if (!StartsWith(lhs, table_name) ||
                lhs.size() <= table_name.size() + 1 ||
                lhs[table_name.size()] != '.') {
                return false;
            }
            const std::string field =
                std::string(TrimWhitespace(lhs.substr(table_name.size() + 1)));
            if (!refii::lua::IsValidLuaIdentifier(field)) {
                return false;
            }
            *field_out = field;
            return true;
        };

    for (std::size_t seed_line = 0; seed_line + 2 < lines.size(); ++seed_line) {
        bool seed_is_local = false;
        std::string table_name;
        std::string seed_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[seed_line],
                                                &seed_is_local,
                                                &table_name,
                                                &seed_rhs) ||
            !seed_is_local ||
            !refii::lua::IsValidLuaIdentifier(table_name) ||
            TrimWhitespace(seed_rhs) != table_name) {
            continue;
        }

        const std::string indent(GetLuaLineIndent(lines[seed_line]));
        std::vector<std::pair<std::string, std::string>> fields;
        std::size_t cursor = seed_line + 1;
        for (; cursor < lines.size(); ++cursor) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[cursor]));
            if (trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent(lines[cursor]) != indent) {
                break;
            }

            bool assign_is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseBytecodeRepairPlainAssignment(lines[cursor],
                                                    &assign_is_local,
                                                    &lhs,
                                                    &rhs) ||
                assign_is_local) {
                break;
            }

            std::string field;
            if (!parse_simple_field_lhs(lhs, table_name, &field)) {
                break;
            }
            if (ContainsIdentifierReference(rhs, table_name)) {
                break;
            }
            fields.emplace_back(std::move(field), std::move(rhs));
            if (fields.size() > 8) {
                break;
            }
        }
        if (fields.empty() || fields.size() > 8 || cursor >= lines.size()) {
            continue;
        }

        while (cursor < lines.size() &&
               TrimWhitespace(StripLineComment(lines[cursor])).empty()) {
            ++cursor;
        }
        if (cursor >= lines.size() || GetLuaLineIndent(lines[cursor]) != indent) {
            continue;
        }

        const std::string_view return_trimmed =
            TrimWhitespace(StripLineComment(lines[cursor]));
        if (!StartsWith(return_trimmed, "return ")) {
            continue;
        }
        std::vector<std::string> return_values =
            SplitBytecodeRepairTopLevelArgs(return_trimmed.substr(7));
        if (return_values.empty()) {
            continue;
        }
        bool replaced_return_value = false;
        std::string table_literal = "{";
        for (std::size_t field_index = 0; field_index < fields.size(); ++field_index) {
            if (field_index != 0) {
                table_literal += ", ";
            }
            table_literal += fields[field_index].first;
            table_literal += " = ";
            table_literal += fields[field_index].second;
        }
        table_literal += "}";
        for (std::string& value : return_values) {
            if (TrimWhitespace(value) == table_name) {
                value = table_literal;
                replaced_return_value = true;
                break;
            }
        }
        if (!replaced_return_value) {
            continue;
        }

        std::string rebuilt_return = indent + "return ";
        for (std::size_t value_index = 0; value_index < return_values.size(); ++value_index) {
            if (value_index != 0) {
                rebuilt_return += ", ";
            }
            rebuilt_return += return_values[value_index];
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[cursor] = std::move(rebuilt_return);
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(seed_line),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(seed_line + 1 + fields.size()));

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_self_seed_field_table_literal_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_self_seed_field_table_literal",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactReturnedBooleanTempTableLiteralBulk(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 6 ||
        std::llabs(context.mismatch.constant_delta) > 3 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4 || lines.size() > 12000) {
        return false;
    }

    struct BulkTempTableChange {
        std::size_t return_line = 0;
        std::vector<std::size_t> erase_lines;
        std::string replacement_return;
    };
    std::vector<BulkTempTableChange> changes;

    const auto is_group_mind_local = [](std::string_view name) {
        name = TrimWhitespace(name);
        return name == "group_mind" ||
               EndsWith(name, "_group_mind") ||
               EndsWith(name, "GroupMind") ||
               EndsWith(name, "groupMind");
    };
    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t line_index = start; line_index < lines.size(); ++line_index) {
            if (!TrimWhitespace(StripLineComment(lines[line_index])).empty()) {
                return line_index;
            }
        }
        return lines.size();
    };
    const auto parse_return_true_identifier =
        [](std::string_view line, std::string* identifier_out) {
            if (identifier_out == nullptr) {
                return false;
            }
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (!StartsWith(trimmed, "return ")) {
                return false;
            }
            std::vector<std::string> values =
                SplitBytecodeRepairTopLevelArgs(trimmed.substr(7));
            if (values.size() != 2 || TrimWhitespace(values.front()) != "true") {
                return false;
            }
            const std::string identifier = std::string(TrimWhitespace(values.back()));
            if (!refii::lua::IsValidLuaIdentifier(identifier)) {
                return false;
            }
            *identifier_out = identifier;
            return true;
        };
    const auto is_declared_between =
        [&](std::string_view identifier, std::size_t begin_line, std::size_t before_line) {
            for (std::size_t scan_line = begin_line;
                 scan_line < before_line && scan_line < lines.size();
                 ++scan_line) {
                bool is_local = false;
                std::vector<std::string> lhs_names;
                std::string rhs;
                if (!ParseBytecodeRepairAssignmentList(lines[scan_line],
                                                       &is_local,
                                                       &lhs_names,
                                                       &rhs) ||
                    !is_local) {
                    continue;
                }
                for (const std::string& lhs : lhs_names) {
                    if (TrimWhitespace(lhs) == identifier) {
                        return true;
                    }
                }
            }
            return false;
        };

    for (std::size_t line_index = 0; line_index + 2 < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index], &is_local, &lhs, &rhs) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(lhs)) {
            continue;
        }

        const std::string indent(GetLuaLineIndent(lines[line_index]));
        if (TrimWhitespace(rhs) == lhs) {
            std::vector<std::pair<std::string, std::string>> fields;
            std::size_t cursor = next_nonempty_line(line_index + 1);
            for (; cursor < lines.size(); cursor = next_nonempty_line(cursor + 1)) {
                if (GetLuaLineIndent(lines[cursor]) != indent) {
                    break;
                }
                bool field_is_local = false;
                std::string field_lhs;
                std::string field_rhs;
                if (!ParseBytecodeRepairPlainAssignment(lines[cursor],
                                                        &field_is_local,
                                                        &field_lhs,
                                                        &field_rhs) ||
                    field_is_local) {
                    break;
                }
                const std::string lhs_prefix = lhs + ".";
                if (!StartsWith(TrimWhitespace(field_lhs), lhs_prefix)) {
                    break;
                }
                const std::string field_name = std::string(
                    TrimWhitespace(std::string_view(field_lhs).substr(lhs_prefix.size())));
                if (!refii::lua::IsValidLuaIdentifier(field_name) ||
                    ContainsIdentifierReference(field_rhs, lhs)) {
                    break;
                }
                fields.emplace_back(field_name, field_rhs);
                if (fields.size() > 8) {
                    break;
                }
            }
            if (fields.empty() || fields.size() > 8 || cursor >= lines.size()) {
                continue;
            }
            std::string returned_identifier;
            if (!parse_return_true_identifier(lines[cursor], &returned_identifier) ||
                returned_identifier != lhs) {
                continue;
            }

            std::string table_literal = "{";
            for (std::size_t field_index = 0; field_index < fields.size(); ++field_index) {
                if (field_index != 0) {
                    table_literal += ", ";
                }
                table_literal += fields[field_index].first;
                table_literal += " = ";
                table_literal += fields[field_index].second;
            }
            table_literal += "}";

            BulkTempTableChange change;
            change.return_line = cursor;
            change.replacement_return =
                std::string(GetLuaLineIndent(lines[cursor])) + "return true, " + table_literal;
            change.erase_lines.push_back(line_index);
            for (std::size_t erase_line = line_index + 1; erase_line < cursor; ++erase_line) {
                if (!TrimWhitespace(StripLineComment(lines[erase_line])).empty()) {
                    change.erase_lines.push_back(erase_line);
                }
            }
            changes.emplace_back(std::move(change));
            continue;
        }

        if (!is_group_mind_local(lhs) || rhs.find('(') == std::string::npos) {
            continue;
        }

        std::size_t if_line = next_nonempty_line(line_index + 1);
        if (if_line >= lines.size() ||
            GetLuaLineIndent(lines[if_line]) != indent ||
            TrimWhitespace(StripLineComment(lines[if_line])) != "if " + lhs + " then") {
            continue;
        }
        std::size_t return_line = next_nonempty_line(if_line + 1);
        if (return_line >= lines.size() ||
            GetLuaLineIndent(lines[return_line]).size() < indent.size()) {
            continue;
        }
        std::string returned_identifier;
        if (!parse_return_true_identifier(lines[return_line], &returned_identifier) ||
            returned_identifier == lhs ||
            is_declared_between(returned_identifier, line_index + 1, return_line)) {
            continue;
        }

        BulkTempTableChange change;
        change.return_line = return_line;
        change.replacement_return =
            std::string(GetLuaLineIndent(lines[return_line])) +
            "return true, {GroupMind = " + lhs + "}";
        changes.emplace_back(std::move(change));
    }

    if (changes.empty()) {
        return false;
    }

    const auto try_commit_change_indices =
        [&](std::vector<std::size_t> indices, std::string_view label) {
            std::sort(indices.begin(),
                      indices.end(),
                      [&](std::size_t lhs, std::size_t rhs) {
                          return changes[lhs].return_line > changes[rhs].return_line;
                      });
            std::vector<std::string> transformed_lines = lines;
            for (std::size_t change_index : indices) {
                if (change_index >= changes.size()) {
                    continue;
                }
                const BulkTempTableChange& change = changes[change_index];
                if (change.return_line >= transformed_lines.size()) {
                    continue;
                }
                transformed_lines[change.return_line] = change.replacement_return;
                std::vector<std::size_t> erase_lines = change.erase_lines;
                std::sort(erase_lines.begin(), erase_lines.end(), std::greater<std::size_t>());
                for (std::size_t erase_line : erase_lines) {
                    if (erase_line < transformed_lines.size()) {
                        transformed_lines.erase(
                            transformed_lines.begin() +
                            static_cast<std::ptrdiff_t>(erase_line));
                    }
                }
            }
            return TryCommitBytecodeParityExactLines(context,
                                                     transformed_lines,
                                                     had_trailing_newline,
                                                     label,
                                                     candidate_source);
        };

    std::vector<std::size_t> all_indices(changes.size());
    for (std::size_t index = 0; index < all_indices.size(); ++index) {
        all_indices[index] = index;
    }
    if (try_commit_change_indices(
            all_indices,
            "bytecode_parity_candidate_returned_boolean_temp_table_literal_bulk_exact")) {
        return true;
    }

    if (changes.size() <= 64) {
        std::vector<std::size_t> line_order(changes.size());
        for (std::size_t index = 0; index < line_order.size(); ++index) {
            line_order[index] = index;
        }
        std::sort(line_order.begin(),
                  line_order.end(),
                  [&](std::size_t lhs, std::size_t rhs) {
                      return changes[lhs].return_line < changes[rhs].return_line;
                  });
        for (std::size_t start = 0; start < line_order.size(); ++start) {
            std::vector<std::size_t> window;
            for (std::size_t length = 1;
                 length <= 4 && start + length <= line_order.size();
                 ++length) {
                window.push_back(line_order[start + length - 1]);
                if (try_commit_change_indices(
                        window,
                        "bytecode_parity_candidate_returned_boolean_temp_table_literal_window_exact")) {
                    return true;
                }
            }
        }
    }

    return false;
}

bool TryRepairNearExactReturnedBooleanReassignedTempTableLiteral(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4 || lines.size() > 12000) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t line_index = start; line_index < lines.size(); ++line_index) {
            if (!TrimWhitespace(StripLineComment(lines[line_index])).empty()) {
                return line_index;
            }
        }
        return lines.size();
    };

    for (std::size_t seed_line = 0; seed_line + 2 < lines.size(); ++seed_line) {
        bool seed_is_local = false;
        std::string temp_name;
        std::string seed_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[seed_line],
                                                &seed_is_local,
                                                &temp_name,
                                                &seed_rhs) ||
            !seed_is_local ||
            !refii::lua::IsValidLuaIdentifier(temp_name) ||
            TrimWhitespace(seed_rhs) != temp_name) {
            continue;
        }

        const std::string indent(GetLuaLineIndent(lines[seed_line]));
        const std::size_t assign_line = next_nonempty_line(seed_line + 1);
        if (assign_line >= lines.size() ||
            GetLuaLineIndent(lines[assign_line]) != indent) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string table_literal;
        if (!ParseBytecodeRepairPlainAssignment(lines[assign_line],
                                                &assign_is_local,
                                                &assign_lhs,
                                                &table_literal) ||
            assign_is_local ||
            TrimWhitespace(assign_lhs) != temp_name) {
            continue;
        }
        table_literal = std::string(TrimWhitespace(table_literal));
        if (table_literal.size() < 2 || table_literal.front() != '{' ||
            table_literal.back() != '}') {
            continue;
        }

        const std::size_t return_line = next_nonempty_line(assign_line + 1);
        if (return_line >= lines.size() ||
            GetLuaLineIndent(lines[return_line]) != indent) {
            continue;
        }
        const std::string_view return_trimmed =
            TrimWhitespace(StripLineComment(lines[return_line]));
        if (!StartsWith(return_trimmed, "return ")) {
            continue;
        }
        std::vector<std::string> return_values =
            SplitBytecodeRepairTopLevelArgs(return_trimmed.substr(7));
        if (return_values.size() != 2 ||
            TrimWhitespace(return_values.front()) != "true" ||
            TrimWhitespace(return_values.back()) != temp_name) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[return_line] = indent + "return true, " + table_literal;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(assign_line));
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(seed_line));

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_boolean_reassigned_temp_table_literal_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_boolean_reassigned_temp_table_literal",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactReturnedGroupMindTempTableLiteral(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4 || lines.size() > 12000) {
        return false;
    }

    struct GroupMindTempTableLiteralChange {
        std::size_t return_line = 0;
        std::optional<std::size_t> seed_line;
        std::optional<std::size_t> field_assignment_line;
        std::string replacement_return;
    };
    std::vector<GroupMindTempTableLiteralChange> bulk_changes;

    const auto is_group_mind_local = [](std::string_view name) {
        name = TrimWhitespace(name);
        return name == "group_mind" ||
               EndsWith(name, "_group_mind") ||
               EndsWith(name, "GroupMind") ||
               EndsWith(name, "groupMind");
    };

    const auto is_declared_between =
        [&](std::string_view identifier, std::size_t begin_line, std::size_t before_line) {
            for (std::size_t scan_line = begin_line;
                 scan_line < before_line && scan_line < lines.size();
                 ++scan_line) {
                bool is_local = false;
                std::vector<std::string> lhs_names;
                std::string rhs;
                if (!ParseBytecodeRepairAssignmentList(lines[scan_line],
                                                       &is_local,
                                                       &lhs_names,
                                                       &rhs) ||
                    !is_local) {
                    continue;
                }
                for (const std::string& lhs : lhs_names) {
                    if (TrimWhitespace(lhs) == identifier) {
                        return true;
                    }
                }
            }
            return false;
        };

    for (std::size_t local_line = 0; local_line + 2 < lines.size(); ++local_line) {
        std::size_t function_start = 0;
        for (std::size_t scan_line = local_line + 1; scan_line > 0; --scan_line) {
            const std::size_t candidate = scan_line - 1;
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[candidate]));
            if (StartsWith(trimmed, "function ") ||
                trimmed.find(" = function(") != std::string_view::npos) {
                function_start = candidate + 1;
                break;
            }
        }

        bool local_is_local = false;
        std::string group_mind_name;
        std::string local_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[local_line],
                                                &local_is_local,
                                                &group_mind_name,
                                                &local_rhs) ||
            !local_is_local ||
            !refii::lua::IsValidLuaIdentifier(group_mind_name) ||
            !is_group_mind_local(group_mind_name) ||
            local_rhs.find('(') == std::string::npos) {
            continue;
        }

        const std::string indent(GetLuaLineIndent(lines[local_line]));
        std::size_t if_line = local_line + 1;
        while (if_line < lines.size() &&
               TrimWhitespace(StripLineComment(lines[if_line])).empty()) {
            ++if_line;
        }
        if (if_line >= lines.size() || GetLuaLineIndent(lines[if_line]) != indent) {
            continue;
        }
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment(lines[if_line]));
        if (if_trimmed != "if " + group_mind_name + " then") {
            continue;
        }

        std::size_t return_line = if_line + 1;
        while (return_line < lines.size() &&
               TrimWhitespace(StripLineComment(lines[return_line])).empty()) {
            ++return_line;
        }
        if (return_line >= lines.size() ||
            GetLuaLineIndent(lines[return_line]).size() < indent.size()) {
            continue;
        }
        std::optional<std::size_t> seed_line;
        std::optional<std::string> seeded_temp;
        {
            bool seed_is_local = false;
            std::string seed_lhs;
            std::string seed_rhs;
            if (ParseBytecodeRepairPlainAssignment(lines[return_line],
                                                   &seed_is_local,
                                                   &seed_lhs,
                                                   &seed_rhs) &&
                seed_is_local &&
                refii::lua::IsValidLuaIdentifier(seed_lhs) &&
                TrimWhitespace(seed_rhs) == seed_lhs) {
                seed_line = return_line;
                seeded_temp = seed_lhs;
                ++return_line;
                while (return_line < lines.size() &&
                       TrimWhitespace(StripLineComment(lines[return_line])).empty()) {
                    ++return_line;
                }
                if (return_line >= lines.size() ||
                    GetLuaLineIndent(lines[return_line]).size() < indent.size()) {
                    continue;
                }
            }
        }
        std::optional<std::size_t> temp_field_assignment_line;
        {
            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (ParseBytecodeRepairPlainAssignment(lines[return_line],
                                                   &assign_is_local,
                                                   &assign_lhs,
                                                   &assign_rhs) &&
                !assign_is_local &&
                TrimWhitespace(assign_rhs) == group_mind_name) {
                const std::string expected_suffix = ".GroupMind";
                const std::string trimmed_lhs = std::string(TrimWhitespace(assign_lhs));
                const bool temp_matches_seed =
                    !seeded_temp.has_value() ||
                    StartsWith(trimmed_lhs, *seeded_temp + ".");
                if (temp_matches_seed && EndsWith(trimmed_lhs, expected_suffix)) {
                    std::size_t maybe_return_line = return_line + 1;
                    while (maybe_return_line < lines.size() &&
                           TrimWhitespace(StripLineComment(lines[maybe_return_line])).empty()) {
                        ++maybe_return_line;
                    }
                    if (maybe_return_line < lines.size() &&
                        GetLuaLineIndent(lines[maybe_return_line]).size() >= indent.size()) {
                        temp_field_assignment_line = return_line;
                        return_line = maybe_return_line;
                    }
                }
            }
        }
        const std::string_view return_trimmed =
            TrimWhitespace(StripLineComment(lines[return_line]));
        if (!StartsWith(return_trimmed, "return ")) {
            continue;
        }
        std::vector<std::string> return_values =
            SplitBytecodeRepairTopLevelArgs(return_trimmed.substr(7));
        if (return_values.size() != 2 ||
            TrimWhitespace(return_values.front()) != "true") {
            continue;
        }
        const std::string returned_identifier =
            std::string(TrimWhitespace(return_values.back()));
        if (!refii::lua::IsValidLuaIdentifier(returned_identifier) ||
            returned_identifier == group_mind_name ||
            (seeded_temp.has_value() && returned_identifier != *seeded_temp) ||
            (!seeded_temp.has_value() &&
             is_declared_between(returned_identifier, function_start, return_line))) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[return_line] =
            std::string(GetLuaLineIndent(lines[return_line])) +
            "return true, {GroupMind = " + group_mind_name + "}";
        if (temp_field_assignment_line.has_value()) {
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(*temp_field_assignment_line));
        }
        if (seed_line.has_value()) {
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(*seed_line));
        }

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_groupmind_temp_table_literal_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_groupmind_temp_table_literal",
                candidate_source)) {
            return true;
        }

        GroupMindTempTableLiteralChange bulk_change;
        bulk_change.return_line = return_line;
        bulk_change.seed_line = seed_line;
        bulk_change.field_assignment_line = temp_field_assignment_line;
        bulk_change.replacement_return =
            std::string(GetLuaLineIndent(lines[return_line])) +
            "return true, {GroupMind = " + group_mind_name + "}";
        bulk_changes.emplace_back(std::move(bulk_change));
    }

    if (!bulk_changes.empty()) {
        std::vector<std::string> transformed_lines = lines;
        std::sort(bulk_changes.begin(),
                  bulk_changes.end(),
                  [](const GroupMindTempTableLiteralChange& lhs,
                     const GroupMindTempTableLiteralChange& rhs) {
                      return lhs.return_line > rhs.return_line;
                  });
        for (const GroupMindTempTableLiteralChange& change : bulk_changes) {
            if (change.return_line >= transformed_lines.size()) {
                continue;
            }
            transformed_lines[change.return_line] = change.replacement_return;
            if (change.field_assignment_line.has_value() &&
                *change.field_assignment_line < transformed_lines.size()) {
                transformed_lines.erase(
                    transformed_lines.begin() +
                    static_cast<std::ptrdiff_t>(*change.field_assignment_line));
            }
            if (change.seed_line.has_value() &&
                *change.seed_line < transformed_lines.size()) {
                transformed_lines.erase(
                    transformed_lines.begin() +
                    static_cast<std::ptrdiff_t>(*change.seed_line));
            }
        }
        if (TryCommitBytecodeParityExactLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_groupmind_temp_table_literal_bulk_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_groupmind_temp_table_literal_bulk",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactReturnedGroupMindConditionalTempSeed(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 6 || lines.size() > 12000) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t line_index = start; line_index < lines.size(); ++line_index) {
            if (!TrimWhitespace(StripLineComment(lines[line_index])).empty()) {
                return line_index;
            }
        }
        return lines.size();
    };
    const auto parse_table_fields =
        [](std::string_view literal,
           std::vector<std::pair<std::string, std::string>>* fields_out) {
            if (fields_out == nullptr) {
                return false;
            }
            literal = TrimWhitespace(literal);
            if (literal.size() < 2 || literal.front() != '{' || literal.back() != '}') {
                return false;
            }
            std::vector<std::pair<std::string, std::string>> fields;
            for (const std::string& raw_field :
                 SplitBytecodeRepairTopLevelArgs(literal.substr(1, literal.size() - 2))) {
                const std::string field = std::string(TrimWhitespace(raw_field));
                const std::size_t equals = field.find('=');
                if (equals == std::string::npos) {
                    return false;
                }
                std::string key =
                    std::string(TrimWhitespace(std::string_view(field).substr(0, equals)));
                std::string value =
                    std::string(TrimWhitespace(std::string_view(field).substr(equals + 1)));
                if (!refii::lua::IsValidLuaIdentifier(key) || value.empty()) {
                    return false;
                }
                fields.emplace_back(std::move(key), std::move(value));
            }
            if (fields.empty() || fields.size() > 8) {
                return false;
            }
            *fields_out = std::move(fields);
            return true;
        };

    for (std::size_t group_line = 0; group_line + 4 < lines.size(); ++group_line) {
        bool group_is_local = false;
        std::string group_name;
        std::string group_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[group_line],
                                                &group_is_local,
                                                &group_name,
                                                &group_rhs) ||
            !group_is_local ||
            !refii::lua::IsValidLuaIdentifier(group_name) ||
            group_rhs.find("GroupMind") == std::string::npos) {
            continue;
        }

        const std::size_t guard_line = next_nonempty_line(group_line + 1);
        if (guard_line >= lines.size() ||
            TrimWhitespace(StripLineComment(lines[guard_line])) !=
                "if " + group_name + " then") {
            continue;
        }

        std::optional<std::size_t> assignment_line;
        std::string temp_name;
        std::vector<std::pair<std::string, std::string>> fields;
        for (std::size_t scan_line = guard_line + 1;
             scan_line < lines.size();
             ++scan_line) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[scan_line]));
            if (StartsWith(trimmed, "function ") ||
                StartsWith(trimmed, "local function ")) {
                break;
            }
            if (trimmed == "return false") {
                break;
            }
            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseBytecodeRepairPlainAssignment(lines[scan_line],
                                                    &assign_is_local,
                                                    &assign_lhs,
                                                    &assign_rhs) ||
                assign_is_local ||
                !refii::lua::IsValidLuaIdentifier(assign_lhs)) {
                continue;
            }
            std::vector<std::pair<std::string, std::string>> parsed_fields;
            if (!parse_table_fields(assign_rhs, &parsed_fields)) {
                continue;
            }
            bool has_true_boolean_field = false;
            bool has_group_mind_field = false;
            for (const auto& field : parsed_fields) {
                if (field.first == "GroupMind") {
                    has_group_mind_field = true;
                }
                if (field.second == "true") {
                    has_true_boolean_field = true;
                }
            }
            if (has_group_mind_field || !has_true_boolean_field) {
                continue;
            }
            assignment_line = scan_line;
            temp_name = std::move(assign_lhs);
            fields = std::move(parsed_fields);
            break;
        }
        if (!assignment_line.has_value() || temp_name.empty()) {
            continue;
        }

        std::optional<std::size_t> return_line;
        for (std::size_t scan_line = *assignment_line + 1; scan_line < lines.size();
             ++scan_line) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[scan_line]));
            if (StartsWith(trimmed, "function ") ||
                StartsWith(trimmed, "local function ") ||
                trimmed == "return false") {
                break;
            }
            if (!StartsWith(trimmed, "return ")) {
                continue;
            }
            std::vector<std::string> return_values =
                SplitBytecodeRepairTopLevelArgs(trimmed.substr(7));
            if (return_values.size() == 2 &&
                TrimWhitespace(return_values.front()) == "true" &&
                TrimWhitespace(return_values.back()) == temp_name) {
                return_line = scan_line;
                break;
            }
        }
        if (!return_line.has_value()) {
            continue;
        }

        std::string seed = std::string(GetLuaLineIndent(lines[guard_line])) +
                           "local " + temp_name + " = {GroupMind = " +
                           group_name;
        for (const auto& field : fields) {
            if (field.second != "true") {
                continue;
            }
            seed += ", ";
            seed += field.first;
            seed += " = false";
            break;
        }
        seed += "}";

        std::vector<std::string> replacement_lines;
        const std::string assignment_indent(GetLuaLineIndent(lines[*assignment_line]));
        for (const auto& field : fields) {
            replacement_lines.push_back(
                assignment_indent + temp_name + "." + field.first + " = " + field.second);
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(guard_line + 1),
            std::move(seed));
        const std::size_t adjusted_assignment_line = *assignment_line + 1;
        transformed_lines.erase(
            transformed_lines.begin() +
            static_cast<std::ptrdiff_t>(adjusted_assignment_line));
        transformed_lines.insert(
            transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(adjusted_assignment_line),
            replacement_lines.begin(),
            replacement_lines.end());

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_groupmind_conditional_temp_seed_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_groupmind_conditional_temp_seed",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactGroupMindAttachedGuardCallLocal(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> transformed_lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (!refii::lua::LocalizeRenderedLuaGroupMindAttachedGuardCallsForParity(
            &transformed_lines)) {
        return false;
    }

    if (TryCommitBytecodeParityExactCurrentFunctionLines(
            context,
            transformed_lines,
            had_trailing_newline,
            "bytecode_parity_candidate_groupmind_attached_guard_call_local",
            candidate_source)) {
        return true;
    }
    return TryCommitBytecodeParityImprovingLines(
        context,
        transformed_lines,
        had_trailing_newline,
        "bytecode_parity_candidate_groupmind_attached_guard_call_local",
        candidate_source);
}

bool TryRepairNearExactLocalTableLiteralSizeHintExpansion(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("NEWTABLE") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto split_top_level_entries =
        [](std::string_view body) -> std::vector<std::string> {
            std::vector<std::string> entries;
            std::size_t entry_start = 0;
            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            for (std::size_t index = 0; index <= body.size(); ++index) {
                const char ch = index < body.size() ? body[index] : ',';
                if (index < body.size()) {
                    if (ch == '\'' && !in_double_quote) {
                        in_single_quote = !in_single_quote;
                        continue;
                    }
                    if (ch == '"' && !in_single_quote) {
                        in_double_quote = !in_double_quote;
                        continue;
                    }
                    if (in_single_quote || in_double_quote) {
                        continue;
                    }
                    if (ch == '(') {
                        ++paren_depth;
                        continue;
                    }
                    if (ch == ')' && paren_depth > 0) {
                        --paren_depth;
                        continue;
                    }
                    if (ch == '{') {
                        ++brace_depth;
                        continue;
                    }
                    if (ch == '}' && brace_depth > 0) {
                        --brace_depth;
                        continue;
                    }
                    if (ch == '[') {
                        ++bracket_depth;
                        continue;
                    }
                    if (ch == ']' && bracket_depth > 0) {
                        --bracket_depth;
                        continue;
                    }
                }
                if (ch != ',' || in_single_quote || in_double_quote ||
                    paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                const std::string_view entry =
                    TrimWhitespace(body.substr(entry_start, index - entry_start));
                if (!entry.empty()) {
                    entries.emplace_back(entry);
                }
                entry_start = index + 1;
            }
            return entries;
        };

    const auto parse_key_value =
        [](std::string_view entry, std::string* key_out, std::string* value_out) {
            if (key_out == nullptr || value_out == nullptr) {
                return false;
            }
            entry = TrimWhitespace(entry);
            const std::size_t equals_pos = entry.find('=');
            if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                equals_pos + 1 >= entry.size()) {
                return false;
            }
            const std::string_view key = TrimWhitespace(entry.substr(0, equals_pos));
            const std::string_view value = TrimWhitespace(entry.substr(equals_pos + 1));
            if (key.empty() || value.empty()) {
                return false;
            }
            if (!refii::lua::IsValidLuaIdentifier(key) &&
                !(key.size() >= 3 && key.front() == '[' && key.back() == ']')) {
                return false;
            }
            *key_out = std::string(key);
            *value_out = std::string(value);
            return true;
        };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        constexpr std::string_view kLocalPrefix = "local ";
        if (!StartsWith(trimmed, kLocalPrefix)) {
            continue;
        }
        const std::size_t eq = trimmed.find('=');
        if (eq == std::string_view::npos || eq <= kLocalPrefix.size() ||
            eq + 1 >= trimmed.size()) {
            continue;
        }
        const std::string local_name =
            std::string(TrimWhitespace(trimmed.substr(kLocalPrefix.size(),
                                                      eq - kLocalPrefix.size())));
        const std::string rhs = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
        if (!refii::lua::IsValidLuaIdentifier(local_name) ||
            rhs.size() < 5 || rhs.front() != '{' || rhs.back() != '}') {
            continue;
        }

        const std::vector<std::string> entries =
            split_top_level_entries(TrimWhitespace(std::string_view(rhs).substr(
                1,
                rhs.size() - 2)));
        if (entries.empty() || entries.size() > 8) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        replacement_lines.reserve(entries.size() + 1);
        const std::string indent = GetLuaLineIndent(lines[line_index]);
        replacement_lines.push_back(indent + "local " + local_name + " = {}");
        bool failed = false;
        for (const std::string& entry : entries) {
            std::string key;
            std::string value;
            if (!parse_key_value(entry, &key, &value)) {
                failed = true;
                break;
            }
            replacement_lines.push_back(
                indent + local_name +
                (refii::lua::IsValidLuaIdentifier(key) ? "." + key : key) +
                " = " + value);
        }
        if (failed) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(line_index));
        transformed_lines.insert(transformed_lines.begin() +
                                     static_cast<std::ptrdiff_t>(line_index),
                                 replacement_lines.begin(),
                                 replacement_lines.end());
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_local_table_literal_size_hint_expansion",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactLocalIdentifierSeedToEmptyTable(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto rhs_is_identifier_path = [](std::string_view rhs) {
        rhs = TrimWhitespace(rhs);
        if (rhs.empty() || rhs == "nil" || rhs == "true" || rhs == "false" ||
            rhs.find(',') != std::string_view::npos ||
            rhs.find(':') != std::string_view::npos ||
            rhs.find('(') != std::string_view::npos ||
            rhs.find(')') != std::string_view::npos ||
            rhs.find('[') != std::string_view::npos ||
            rhs.find(']') != std::string_view::npos ||
            rhs.find('{') != std::string_view::npos ||
            rhs.find('}') != std::string_view::npos) {
            return false;
        }
        std::size_t token_start = 0;
        while (token_start < rhs.size()) {
            const std::size_t dot = rhs.find('.', token_start);
            const std::string_view token =
                dot == std::string_view::npos
                    ? rhs.substr(token_start)
                    : rhs.substr(token_start, dot - token_start);
            if (!refii::lua::IsValidLuaIdentifier(TrimWhitespace(token))) {
                return false;
            }
            if (dot == std::string_view::npos) {
                break;
            }
            token_start = dot + 1;
        }
        return true;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        constexpr std::string_view kLocalPrefix = "local ";
        if (!StartsWith(trimmed, kLocalPrefix)) {
            continue;
        }
        const std::size_t eq = trimmed.find('=');
        if (eq == std::string_view::npos || eq <= kLocalPrefix.size() ||
            eq + 1 >= trimmed.size()) {
            continue;
        }
        const std::string local_name =
            std::string(TrimWhitespace(trimmed.substr(kLocalPrefix.size(),
                                                      eq - kLocalPrefix.size())));
        const std::string rhs = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
        if (!refii::lua::IsValidLuaIdentifier(local_name) ||
            !rhs_is_identifier_path(rhs)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + "local " + local_name + " = {}";
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_local_identifier_seed_empty_table",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactTableLiteralAssignmentToFieldWrites(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("NEWTABLE") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 32 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto parse_assignment =
        [](std::string_view line, std::string* lhs_out, std::string* rhs_out) {
            if (lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *lhs_out = std::string();
            *rhs_out = std::string();
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (trimmed.empty() || StartsWith(trimmed, "local ")) {
                return false;
            }
            const std::size_t eq = trimmed.find('=');
            if (eq == std::string_view::npos || eq == 0 || eq + 1 >= trimmed.size() ||
                trimmed.find("==") != std::string_view::npos ||
                trimmed.find("~=") != std::string_view::npos ||
                trimmed.find("<=") != std::string_view::npos ||
                trimmed.find(">=") != std::string_view::npos) {
                return false;
            }
            *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, eq)));
            *rhs_out = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto lhs_is_receiver = [](std::string_view lhs) {
        lhs = TrimWhitespace(lhs);
        if (lhs.empty() || refii::lua::IsRenderTemporaryName(lhs) ||
            lhs.find(',') != std::string_view::npos ||
            lhs.find(':') != std::string_view::npos ||
            lhs.find('(') != std::string_view::npos ||
            lhs.find(')') != std::string_view::npos ||
            lhs.find('[') != std::string_view::npos ||
            lhs.find(']') != std::string_view::npos) {
            return false;
        }
        std::size_t token_start = 0;
        while (token_start < lhs.size()) {
            const std::size_t dot = lhs.find('.', token_start);
            const std::string_view token =
                dot == std::string_view::npos
                    ? lhs.substr(token_start)
                    : lhs.substr(token_start, dot - token_start);
            if (!refii::lua::IsValidLuaIdentifier(TrimWhitespace(token))) {
                return false;
            }
            if (dot == std::string_view::npos) {
                break;
            }
            token_start = dot + 1;
        }
        return true;
    };

    const auto split_top_level_entries =
        [](std::string_view body) -> std::vector<std::string> {
            std::vector<std::string> entries;
            std::size_t entry_start = 0;
            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            for (std::size_t index = 0; index <= body.size(); ++index) {
                const char ch = index < body.size() ? body[index] : ',';
                if (index < body.size()) {
                    if (ch == '\'' && !in_double_quote) {
                        in_single_quote = !in_single_quote;
                        continue;
                    }
                    if (ch == '"' && !in_single_quote) {
                        in_double_quote = !in_double_quote;
                        continue;
                    }
                    if (in_single_quote || in_double_quote) {
                        continue;
                    }
                    if (ch == '(') {
                        ++paren_depth;
                        continue;
                    }
                    if (ch == ')' && paren_depth > 0) {
                        --paren_depth;
                        continue;
                    }
                    if (ch == '{') {
                        ++brace_depth;
                        continue;
                    }
                    if (ch == '}' && brace_depth > 0) {
                        --brace_depth;
                        continue;
                    }
                    if (ch == '[') {
                        ++bracket_depth;
                        continue;
                    }
                    if (ch == ']' && bracket_depth > 0) {
                        --bracket_depth;
                        continue;
                    }
                }
                if (ch != ',' || in_single_quote || in_double_quote ||
                    paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }
                const std::string_view entry =
                    TrimWhitespace(body.substr(entry_start, index - entry_start));
                if (!entry.empty()) {
                    entries.emplace_back(entry);
                }
                entry_start = index + 1;
            }
            return entries;
        };

    const auto parse_entry =
        [](std::string_view entry, std::string* key_out, std::string* value_out) {
            if (key_out == nullptr || value_out == nullptr) {
                return false;
            }
            entry = TrimWhitespace(entry);
            const std::size_t eq = entry.find('=');
            if (eq == std::string_view::npos || eq == 0 || eq + 1 >= entry.size()) {
                return false;
            }
            const std::string_view key = TrimWhitespace(entry.substr(0, eq));
            const std::string_view value = TrimWhitespace(entry.substr(eq + 1));
            if (key.empty() || value.empty()) {
                return false;
            }
            if (!refii::lua::IsValidLuaIdentifier(key) &&
                !(key.size() >= 3 && key.front() == '[' && key.back() == ']')) {
                return false;
            }
            *key_out = std::string(key);
            *value_out = std::string(value);
            return true;
        };

    std::vector<std::string> batch_transformed_lines = lines;
    bool batch_changed = false;
    std::ptrdiff_t batch_line_delta = 0;

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string lhs;
        std::string rhs;
        if (!parse_assignment(lines[line_index], &lhs, &rhs) ||
            !lhs_is_receiver(lhs)) {
            continue;
        }

        const std::string_view rhs_trimmed = TrimWhitespace(rhs);
        if (rhs_trimmed.size() < 3 || rhs_trimmed.front() != '{' ||
            rhs_trimmed.back() != '}') {
            continue;
        }
        const std::vector<std::string> entries =
            split_top_level_entries(TrimWhitespace(rhs_trimmed.substr(
                1,
                rhs_trimmed.size() - 2)));
        if (entries.empty() || entries.size() > 8) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        replacement_lines.reserve(entries.size());
        const std::string indent = GetLuaLineIndent(lines[line_index]);
        bool failed = false;
        for (const std::string& entry : entries) {
            std::string key;
            std::string value;
            if (!parse_entry(entry, &key, &value)) {
                failed = true;
                break;
            }
            replacement_lines.push_back(
                indent + lhs + (refii::lua::IsValidLuaIdentifier(key) ? "." + key : key) +
                " = " + value);
        }
        if (failed) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(line_index));
        transformed_lines.insert(transformed_lines.begin() +
                                     static_cast<std::ptrdiff_t>(line_index),
                                 replacement_lines.begin(),
                                 replacement_lines.end());
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_table_literal_assignment_field_writes",
                candidate_source)) {
            return true;
        }

        const std::ptrdiff_t batch_line_index =
            static_cast<std::ptrdiff_t>(line_index) + batch_line_delta;
        if (batch_line_index < 0 ||
            static_cast<std::size_t>(batch_line_index) >= batch_transformed_lines.size()) {
            continue;
        }
        batch_transformed_lines.erase(batch_transformed_lines.begin() + batch_line_index);
        batch_transformed_lines.insert(batch_transformed_lines.begin() + batch_line_index,
                                       replacement_lines.begin(),
                                       replacement_lines.end());
        batch_line_delta +=
            static_cast<std::ptrdiff_t>(replacement_lines.size()) - 1;
        batch_changed = true;
    }

    if (batch_changed &&
        TryCommitBytecodeParityImprovingLines(
            context,
            batch_transformed_lines,
            had_trailing_newline,
            "bytecode_parity_candidate_table_literal_assignment_field_writes_batch",
            candidate_source)) {
        return true;
    }

    return false;
}

bool TryRepairNearExactLocalTableLiteralToFieldWrites(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("NEWTABLE") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 32 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto parse_table_entry =
        [](std::string_view entry, std::string* key_out, std::string* value_out) {
            if (key_out == nullptr || value_out == nullptr) {
                return false;
            }
            entry = TrimWhitespace(entry);
            const std::size_t eq = FindBytecodeRepairTopLevelToken(entry, " = ");
            if (eq == std::string_view::npos || eq == 0 || eq + 3 >= entry.size()) {
                return false;
            }
            const std::string key = std::string(TrimWhitespace(entry.substr(0, eq)));
            const std::string value = std::string(TrimWhitespace(entry.substr(eq + 3)));
            if (key.empty() || value.empty() ||
                (!refii::lua::IsValidLuaIdentifier(key) &&
                 !(key.size() >= 3 && key.front() == '[' && key.back() == ']'))) {
                return false;
            }
            *key_out = key;
            *value_out = value;
            return true;
        };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string local_name;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &local_name,
                                                &rhs) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(local_name) ||
            refii::lua::IsRenderTemporaryName(local_name)) {
            continue;
        }
        rhs = std::string(TrimWhitespace(rhs));
        if (rhs.size() < 3 || rhs.front() != '{' || rhs.back() != '}') {
            continue;
        }
        const std::vector<std::string> entries =
            SplitBytecodeRepairTopLevelArgs(rhs.substr(1, rhs.size() - 2));
        if (entries.empty() || entries.size() > 8) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        replacement_lines.reserve(entries.size() + 1);
        const std::string indent = GetLuaLineIndent(lines[line_index]);
        replacement_lines.push_back(indent + "local " + local_name + " = {}");
        bool failed = false;
        for (const std::string& entry : entries) {
            std::string key;
            std::string value;
            if (!parse_table_entry(entry, &key, &value)) {
                failed = true;
                break;
            }
            replacement_lines.push_back(
                indent + local_name +
                (refii::lua::IsValidLuaIdentifier(key) ? "." + key : key) +
                " = " + value);
        }
        if (failed) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(line_index));
        transformed_lines.insert(transformed_lines.begin() +
                                     static_cast<std::ptrdiff_t>(line_index),
                                 replacement_lines.begin(),
                                 replacement_lines.end());
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_local_table_literal_field_writes_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_local_table_literal_field_writes",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactDeadInlinedTempTableLiteralArgs(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos &&
        context.mismatch.note.find("NEWTABLE") == std::string::npos &&
        context.mismatch.note.find("SETTABLE") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 64 ||
        std::llabs(context.mismatch.constant_delta) > 8 ||
        std::llabs(context.mismatch.local_delta) > 8) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 3 || lines.size() > 16000) {
        return false;
    }

    struct DeadTempTableLiteralArgChange {
        std::size_t use_line = 0;
        std::vector<std::size_t> erase_lines;
    };
    std::vector<DeadTempTableLiteralArgChange> changes;

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t line_index = start; line_index < lines.size(); ++line_index) {
            if (!TrimWhitespace(StripLineComment(lines[line_index])).empty()) {
                return line_index;
            }
        }
        return lines.size();
    };
    const auto is_empty_table_seed =
        [](std::string_view line, std::string* local_name_out) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseBytecodeRepairPlainAssignment(line, &is_local, &lhs, &rhs) ||
                !is_local ||
                !refii::lua::IsValidLuaIdentifier(lhs) ||
                TrimWhitespace(rhs) != "{}") {
                return false;
            }
            if (local_name_out != nullptr) {
                *local_name_out = lhs;
            }
            return true;
        };
    const auto line_contains_exact_table_literal =
        [](std::string_view line,
           const std::vector<std::pair<std::string, std::string>>& fields) {
            if (fields.empty()) {
                return false;
            }
            std::string table_literal = "{";
            for (std::size_t field_index = 0; field_index < fields.size(); ++field_index) {
                if (field_index != 0) {
                    table_literal += ", ";
                }
                table_literal += fields[field_index].first;
                table_literal += " = ";
                table_literal += fields[field_index].second;
            }
            table_literal += "}";
            return StripLineComment(line).find(table_literal) != std::string_view::npos;
        };

    for (std::size_t line_index = 0; line_index + 2 < lines.size(); ++line_index) {
        std::string temp_name;
        if (!is_empty_table_seed(lines[line_index], &temp_name)) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        std::vector<std::pair<std::string, std::string>> fields;
        std::vector<std::size_t> erase_lines;
        erase_lines.push_back(line_index);

        std::size_t cursor = next_nonempty_line(line_index + 1);
        for (; cursor < lines.size(); cursor = next_nonempty_line(cursor + 1)) {
            if (GetLuaLineIndent(lines[cursor]) != indent) {
                break;
            }
            bool field_is_local = false;
            std::string field_lhs;
            std::string field_rhs;
            if (!ParseBytecodeRepairPlainAssignment(lines[cursor],
                                                    &field_is_local,
                                                    &field_lhs,
                                                    &field_rhs) ||
                field_is_local) {
                break;
            }
            const std::string lhs_prefix = temp_name + ".";
            if (!StartsWith(TrimWhitespace(field_lhs), lhs_prefix)) {
                break;
            }
            const std::string field_name = std::string(
                TrimWhitespace(std::string_view(field_lhs).substr(lhs_prefix.size())));
            if (!refii::lua::IsValidLuaIdentifier(field_name) ||
                ContainsIdentifierReference(field_rhs, temp_name)) {
                fields.clear();
                break;
            }
            fields.emplace_back(field_name, std::string(TrimWhitespace(field_rhs)));
            erase_lines.push_back(cursor);
            if (fields.size() > 12) {
                fields.clear();
                break;
            }
        }

        if (fields.empty() || cursor >= lines.size()) {
            continue;
        }
        const std::string_view use_line = StripLineComment(lines[cursor]);
        if (ContainsIdentifierReference(use_line, temp_name) ||
            !line_contains_exact_table_literal(use_line, fields)) {
            continue;
        }

        DeadTempTableLiteralArgChange change;
        change.use_line = cursor;
        change.erase_lines = std::move(erase_lines);
        changes.emplace_back(std::move(change));
    }

    if (changes.empty()) {
        return false;
    }

    const auto try_commit_change_indices =
        [&](std::vector<std::size_t> indices, std::string_view label) {
            std::sort(indices.begin(),
                      indices.end(),
                      [&](std::size_t lhs, std::size_t rhs) {
                          return changes[lhs].use_line > changes[rhs].use_line;
                      });
            std::vector<std::string> transformed_lines = lines;
            for (std::size_t change_index : indices) {
                if (change_index >= changes.size()) {
                    continue;
                }
                std::vector<std::size_t> erase_lines = changes[change_index].erase_lines;
                std::sort(erase_lines.begin(), erase_lines.end(), std::greater<std::size_t>());
                for (std::size_t erase_line : erase_lines) {
                    if (erase_line < transformed_lines.size()) {
                        transformed_lines.erase(
                            transformed_lines.begin() +
                            static_cast<std::ptrdiff_t>(erase_line));
                    }
                }
            }
            return TryCommitBytecodeParityExactLines(
                context,
                transformed_lines,
                had_trailing_newline,
                label,
                candidate_source);
        };

    std::vector<std::size_t> all_indices(changes.size());
    for (std::size_t index = 0; index < all_indices.size(); ++index) {
        all_indices[index] = index;
    }
    if (try_commit_change_indices(
            all_indices,
            "bytecode_parity_candidate_dead_inlined_temp_table_literal_args_exact")) {
        return true;
    }

    if (changes.size() <= 96) {
        std::vector<std::size_t> line_order(changes.size());
        for (std::size_t index = 0; index < line_order.size(); ++index) {
            line_order[index] = index;
        }
        std::sort(line_order.begin(),
                  line_order.end(),
                  [&](std::size_t lhs, std::size_t rhs) {
                      return changes[lhs].use_line < changes[rhs].use_line;
                  });
        for (std::size_t start = 0; start < line_order.size(); ++start) {
            std::vector<std::size_t> window;
            for (std::size_t length = 1;
                 length <= 6 && start + length <= line_order.size();
                 ++length) {
                window.push_back(line_order[start + length - 1]);
                if (try_commit_change_indices(
                        window,
                        "bytecode_parity_candidate_dead_inlined_temp_table_literal_args_window_exact")) {
                    return true;
                }
            }
        }
    }

    return false;
}

bool TryRepairNearExactDuplicateGlobalTableSelfSeed(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty() || lines.size() > 4096) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &lhs,
                                                &rhs) ||
            is_local ||
            !refii::lua::IsValidLuaIdentifier(lhs) ||
            TrimWhitespace(rhs).size() < 2) {
            continue;
        }

        const std::string rhs_trimmed = std::string(TrimWhitespace(rhs));
        if (rhs_trimmed.front() != '{' || rhs_trimmed.back() != '}') {
            continue;
        }
        const std::vector<std::string> entries =
            SplitBytecodeRepairTopLevelArgs(
                rhs_trimmed.substr(1, rhs_trimmed.size() - 2));
        if (entries.size() != 2) {
            continue;
        }

        const auto parse_entry =
            [](std::string_view entry,
               std::string* key_out,
               std::string* value_out) {
            const std::size_t eq = FindBytecodeRepairTopLevelToken(entry, " = ");
            if (eq == std::string_view::npos || eq == 0 || eq + 3 >= entry.size()) {
                return false;
            }
            const std::string key = std::string(TrimWhitespace(entry.substr(0, eq)));
            const std::string value =
                std::string(TrimWhitespace(entry.substr(eq + 3)));
            if (!refii::lua::IsValidLuaIdentifier(key) || value.empty()) {
                return false;
            }
            if (key_out != nullptr) {
                *key_out = key;
            }
            if (value_out != nullptr) {
                *value_out = value;
            }
            return true;
        };

        std::string first_key;
        std::string first_value;
        std::string second_key;
        std::string second_value;
        if (!parse_entry(entries[0], &first_key, &first_value) ||
            !parse_entry(entries[1], &second_key, &second_value) ||
            first_key != second_key ||
            first_value != "{}" ||
            second_value != lhs) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + lhs + "." + first_key + " = {}";
        if (TryCommitBytecodeParityExactLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_duplicate_global_table_self_seed_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_duplicate_global_table_self_seed",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactObjectScalarIntoSequenceTableField(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("SETTABLE") == std::string::npos &&
        context.mismatch.note.find("NEWTABLE") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const auto parse_dotted_lhs =
        [](std::string_view lhs, std::string* receiver_out, std::string* field_out) {
            if (receiver_out == nullptr || field_out == nullptr) {
                return false;
            }
            lhs = TrimWhitespace(lhs);
            const std::size_t dot = lhs.rfind('.');
            if (dot == std::string_view::npos || dot == 0 || dot + 1 >= lhs.size()) {
                return false;
            }
            const std::string receiver = std::string(lhs.substr(0, dot));
            const std::string field = std::string(lhs.substr(dot + 1));
            if (!IsBytecodeRepairSafeDottedPrefix(receiver) ||
                !refii::lua::IsValidLuaIdentifier(field)) {
                return false;
            }
            *receiver_out = receiver;
            *field_out = field;
            return true;
        };

    const auto parse_table_entry_fields =
        [](std::string_view line,
           std::vector<std::pair<std::string, std::string>>* fields_out) {
            if (fields_out == nullptr) {
                return false;
            }
            std::string text = std::string(TrimWhitespace(StripLineComment(line)));
            if (!text.empty() && text.back() == ',') {
                text.pop_back();
                text = std::string(TrimWhitespace(text));
            }
            if (text.size() < 2 || text.front() != '{' || text.back() != '}') {
                return false;
            }
            const std::vector<std::string> entries =
                SplitBytecodeRepairTopLevelArgs(text.substr(1, text.size() - 2));
            if (entries.empty() || entries.size() > 8) {
                return false;
            }
            std::vector<std::pair<std::string, std::string>> fields;
            for (const std::string& entry : entries) {
                const std::size_t eq = FindBytecodeRepairTopLevelToken(entry, " = ");
                if (eq == std::string_view::npos || eq == 0 || eq + 3 >= entry.size()) {
                    return false;
                }
                const std::string key = std::string(TrimWhitespace(entry.substr(0, eq)));
                const std::string value =
                    std::string(TrimWhitespace(entry.substr(eq + 3)));
                if (!refii::lua::IsValidLuaIdentifier(key) || value.empty()) {
                    return false;
                }
                fields.emplace_back(key, value);
            }
            *fields_out = std::move(fields);
            return true;
        };

    const auto rebuild_table_entry =
        [](const std::string& indent,
           const std::vector<std::pair<std::string, std::string>>& fields,
           bool trailing_comma) {
            std::string line = indent + "{";
            for (std::size_t index = 0; index < fields.size(); ++index) {
                if (index != 0) {
                    line += ", ";
                }
                line += fields[index].first;
                line += " = ";
                line += fields[index].second;
            }
            line += "}";
            if (trailing_comma) {
                line += ",";
            }
            return line;
        };

    for (std::size_t scalar_line = 0; scalar_line + 3 < lines.size(); ++scalar_line) {
        bool scalar_is_local = false;
        std::string scalar_lhs;
        std::string scalar_value;
        if (!ParseBytecodeRepairPlainAssignment(lines[scalar_line],
                                                &scalar_is_local,
                                                &scalar_lhs,
                                                &scalar_value) ||
            scalar_is_local) {
            continue;
        }
        std::string scalar_receiver;
        std::string scalar_field;
        if (!parse_dotted_lhs(scalar_lhs, &scalar_receiver, &scalar_field)) {
            continue;
        }

        const std::size_t table_line = scalar_line + 1;
        bool table_is_local = false;
        std::string table_lhs;
        std::string table_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[table_line],
                                                &table_is_local,
                                                &table_lhs,
                                                &table_rhs) ||
            table_is_local ||
            !StartsWith(TrimWhitespace(table_rhs), "{")) {
            continue;
        }
        std::string table_receiver;
        std::string table_field;
        if (!parse_dotted_lhs(table_lhs, &table_receiver, &table_field) ||
            table_receiver != scalar_receiver ||
            table_field == scalar_field) {
            continue;
        }
        if (GetLuaLineIndent(lines[scalar_line]) != GetLuaLineIndent(lines[table_line])) {
            continue;
        }

        std::size_t close_line = table_line + 1;
        while (close_line < lines.size()) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[close_line]));
            if (trimmed == "}" || trimmed == "},") {
                break;
            }
            ++close_line;
        }
        if (close_line >= lines.size() || close_line <= table_line + 1 ||
            close_line - table_line > 16) {
            continue;
        }

        std::vector<std::vector<std::pair<std::string, std::string>>> table_entries;
        table_entries.reserve(close_line - table_line - 1);
        bool failed = false;
        for (std::size_t entry_line = table_line + 1; entry_line < close_line; ++entry_line) {
            std::vector<std::pair<std::string, std::string>> fields;
            if (!parse_table_entry_fields(lines[entry_line], &fields)) {
                failed = true;
                break;
            }
            table_entries.push_back(std::move(fields));
        }
        if (failed || table_entries.empty()) {
            continue;
        }

        std::vector<std::string> shifted_values;
        shifted_values.reserve(table_entries.size());
        shifted_values.push_back(std::string(TrimWhitespace(scalar_value)));
        for (std::size_t entry_index = 0;
             entry_index + 1 < table_entries.size();
             ++entry_index) {
            bool found = false;
            for (const auto& field : table_entries[entry_index]) {
                if (field.first == scalar_field) {
                    shifted_values.push_back(field.second);
                    found = true;
                    break;
                }
            }
            if (!found) {
                failed = true;
                break;
            }
        }
        if (failed || shifted_values.size() != table_entries.size()) {
            continue;
        }

        for (std::size_t entry_index = 0; entry_index < table_entries.size(); ++entry_index) {
            std::vector<std::pair<std::string, std::string>> rebuilt_fields;
            rebuilt_fields.emplace_back(scalar_field, shifted_values[entry_index]);
            for (const auto& field : table_entries[entry_index]) {
                if (field.first != scalar_field) {
                    rebuilt_fields.push_back(field);
                }
            }
            table_entries[entry_index] = std::move(rebuilt_fields);
        }

        std::vector<std::string> transformed_lines = lines;
        for (std::size_t entry_index = 0; entry_index < table_entries.size(); ++entry_index) {
            const std::size_t entry_line = table_line + 1 + entry_index;
            const bool trailing_comma =
                TrimWhitespace(StripLineComment(lines[entry_line])).back() == ',';
            transformed_lines[entry_line] =
                rebuild_table_entry(GetLuaLineIndent(lines[entry_line]),
                                    table_entries[entry_index],
                                    trailing_comma);
        }
        if (scalar_line >= 2) {
            bool empty_is_local = false;
            bool alias_is_local = false;
            std::string empty_lhs;
            std::string empty_rhs;
            std::string alias_lhs;
            std::string alias_rhs;
            std::string empty_receiver;
            std::string empty_field;
            std::string alias_receiver;
            std::string alias_field;
            if (ParseBytecodeRepairPlainAssignment(lines[scalar_line - 2],
                                                   &empty_is_local,
                                                   &empty_lhs,
                                                   &empty_rhs) &&
                ParseBytecodeRepairPlainAssignment(lines[scalar_line - 1],
                                                   &alias_is_local,
                                                   &alias_lhs,
                                                   &alias_rhs) &&
                !empty_is_local && !alias_is_local &&
                TrimWhitespace(empty_rhs) == "{}" &&
                TrimWhitespace(alias_rhs) == scalar_receiver &&
                parse_dotted_lhs(empty_lhs, &empty_receiver, &empty_field) &&
                parse_dotted_lhs(alias_lhs, &alias_receiver, &alias_field) &&
                empty_receiver == scalar_receiver &&
                alias_receiver == scalar_receiver) {
                std::string method_param;
                for (std::size_t header_line = scalar_line; header_line > 0; --header_line) {
                    const std::string_view header =
                        TrimWhitespace(StripLineComment(lines[header_line - 1]));
                    if (!StartsWith(header, "function ")) {
                        continue;
                    }
                    const std::size_t open = header.find('(');
                    const std::size_t close = header.rfind(')');
                    if (open == std::string_view::npos ||
                        close == std::string_view::npos ||
                        close <= open) {
                        break;
                    }
                    const std::vector<std::string> params =
                        SplitBytecodeRepairTopLevelArgs(header.substr(open + 1,
                                                                      close - open - 1));
                    for (const std::string& param : params) {
                        const std::string trimmed_param =
                            std::string(TrimWhitespace(param));
                        if (trimmed_param != "self" &&
                            refii::lua::IsValidLuaIdentifier(trimmed_param)) {
                            method_param = trimmed_param;
                            break;
                        }
                    }
                    break;
                }
                if (!method_param.empty()) {
                    transformed_lines[scalar_line - 2] =
                        GetLuaLineIndent(lines[scalar_line - 2]) + empty_lhs + " = " +
                        method_param;
                    transformed_lines[scalar_line - 1] =
                        GetLuaLineIndent(lines[scalar_line - 1]) + alias_lhs + " = {}";
                }
            }
        }
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(scalar_line));

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_object_scalar_sequence_table_field_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_object_scalar_sequence_table_field",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactConstructorParamEmptyAliasSwap(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("SETTABLE") == std::string::npos &&
        context.mismatch.note.find("NEWTABLE") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 3) {
        return false;
    }

    const auto parse_dotted_lhs =
        [](std::string_view lhs, std::string* receiver_out) {
            lhs = TrimWhitespace(lhs);
            const std::size_t dot = lhs.rfind('.');
            if (receiver_out == nullptr ||
                dot == std::string_view::npos ||
                dot == 0 ||
                dot + 1 >= lhs.size()) {
                return false;
            }
            const std::string receiver = std::string(lhs.substr(0, dot));
            const std::string field = std::string(lhs.substr(dot + 1));
            if (!IsBytecodeRepairSafeDottedPrefix(receiver) ||
                !refii::lua::IsValidLuaIdentifier(field)) {
                return false;
            }
            *receiver_out = receiver;
            return true;
        };

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool empty_is_local = false;
        bool alias_is_local = false;
        std::string empty_lhs;
        std::string empty_rhs;
        std::string alias_lhs;
        std::string alias_rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &empty_is_local,
                                                &empty_lhs,
                                                &empty_rhs) ||
            !ParseBytecodeRepairPlainAssignment(lines[line_index + 1],
                                                &alias_is_local,
                                                &alias_lhs,
                                                &alias_rhs) ||
            empty_is_local || alias_is_local ||
            TrimWhitespace(empty_rhs) != "{}") {
            continue;
        }

        std::string empty_receiver;
        std::string alias_receiver;
        if (!parse_dotted_lhs(empty_lhs, &empty_receiver) ||
            !parse_dotted_lhs(alias_lhs, &alias_receiver) ||
            empty_receiver != alias_receiver ||
            TrimWhitespace(alias_rhs) != empty_receiver) {
            continue;
        }

        std::string method_param;
        for (std::size_t header_line = line_index; header_line > 0; --header_line) {
            const std::string_view header =
                TrimWhitespace(StripLineComment(lines[header_line - 1]));
            if (!StartsWith(header, "function ")) {
                continue;
            }
            const std::size_t open = header.find('(');
            const std::size_t close = header.rfind(')');
            if (open == std::string_view::npos ||
                close == std::string_view::npos ||
                close <= open) {
                break;
            }
            const std::vector<std::string> params =
                SplitBytecodeRepairTopLevelArgs(header.substr(open + 1,
                                                              close - open - 1));
            for (const std::string& param : params) {
                const std::string trimmed_param =
                    std::string(TrimWhitespace(param));
                if (trimmed_param != "self" &&
                    refii::lua::IsValidLuaIdentifier(trimmed_param)) {
                    method_param = trimmed_param;
                    break;
                }
            }
            break;
        }
        if (method_param.empty()) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + empty_lhs + " = " + method_param;
        transformed_lines[line_index + 1] =
            GetLuaLineIndent(lines[line_index + 1]) + alias_lhs + " = {}";
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_constructor_param_empty_alias_swap_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_constructor_param_empty_alias_swap",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactConstructorEmptyFieldFromParameter(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("SETTABLE") == std::string::npos &&
        context.mismatch.note.find("NEWTABLE") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 3) {
        return false;
    }

    const auto parse_dotted_lhs =
        [](std::string_view lhs, std::string* receiver_out) {
            lhs = TrimWhitespace(lhs);
            const std::size_t dot = lhs.rfind('.');
            if (receiver_out == nullptr ||
                dot == std::string_view::npos ||
                dot == 0 ||
                dot + 1 >= lhs.size()) {
                return false;
            }
            const std::string receiver = std::string(lhs.substr(0, dot));
            const std::string field = std::string(lhs.substr(dot + 1));
            if (!IsBytecodeRepairSafeDottedPrefix(receiver) ||
                !refii::lua::IsValidLuaIdentifier(field)) {
                return false;
            }
            *receiver_out = receiver;
            return true;
        };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index],
                                                &is_local,
                                                &lhs,
                                                &rhs) ||
            is_local ||
            TrimWhitespace(rhs) != "{}") {
            continue;
        }

        std::string receiver;
        if (!parse_dotted_lhs(lhs, &receiver)) {
            continue;
        }

        std::vector<std::string> method_params;
        for (std::size_t header_line = line_index; header_line > 0; --header_line) {
            const std::string_view header =
                TrimWhitespace(StripLineComment(lines[header_line - 1]));
            if (!StartsWith(header, "function ")) {
                continue;
            }
            const std::size_t open = header.find('(');
            const std::size_t close = header.rfind(')');
            if (open == std::string_view::npos ||
                close == std::string_view::npos ||
                close <= open) {
                break;
            }
            const std::vector<std::string> params =
                SplitBytecodeRepairTopLevelArgs(header.substr(open + 1,
                                                              close - open - 1));
            for (const std::string& param : params) {
                const std::string trimmed_param =
                    std::string(TrimWhitespace(param));
                if (trimmed_param != "self" &&
                    refii::lua::IsValidLuaIdentifier(trimmed_param)) {
                    method_params.push_back(trimmed_param);
                }
            }
            break;
        }
        if (method_params.empty()) {
            continue;
        }

        for (const std::string& method_param : method_params) {
            std::vector<std::string> transformed_lines = lines;
            transformed_lines[line_index] =
                GetLuaLineIndent(lines[line_index]) + lhs + " = " + method_param;
            if (TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_constructor_empty_field_parameter_exact",
                    candidate_source)) {
                return true;
            }
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_constructor_empty_field_parameter",
                    candidate_source)) {
                return true;
            }

            const std::size_t cluster_end =
                std::min(lines.size(), line_index + static_cast<std::size_t>(64));
            for (std::size_t patch_line = line_index + 1;
                 patch_line < cluster_end;
                 ++patch_line) {
                const std::string_view trimmed =
                    TrimWhitespace(StripLineComment(lines[patch_line]));
                if (StartsWith(trimmed, "function ")) {
                    break;
                }

                bool patch_is_local = false;
                std::string patch_lhs;
                std::string patch_rhs;
                if (!ParseBytecodeRepairPlainAssignment(lines[patch_line],
                                                        &patch_is_local,
                                                        &patch_lhs,
                                                        &patch_rhs) ||
                    patch_is_local ||
                    TrimWhitespace(patch_rhs) != receiver) {
                    continue;
                }

                std::string patch_receiver;
                if (!parse_dotted_lhs(patch_lhs, &patch_receiver) ||
                    patch_receiver != receiver) {
                    continue;
                }

                std::vector<std::string> paired_lines = transformed_lines;
                paired_lines[patch_line] =
                    GetLuaLineIndent(lines[patch_line]) + patch_lhs + " = {}";
                if (TryCommitBytecodeParityExactCurrentFunctionLines(
                        context,
                        paired_lines,
                        had_trailing_newline,
                        "bytecode_parity_candidate_constructor_empty_field_parameter_self_field_table_exact",
                        candidate_source)) {
                    return true;
                }
                if (TryCommitBytecodeParityImprovingLines(
                        context,
                        paired_lines,
                        had_trailing_newline,
                        "bytecode_parity_candidate_constructor_empty_field_parameter_self_field_table",
                        candidate_source)) {
                    return true;
                }
            }
        }
    }

    return false;
}

bool TryRepairNearExactRedundantIndexedTableAssignmentBeforeFieldWrite(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("SETTABLE") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index], &is_local, &lhs, &rhs) ||
            is_local) {
            continue;
        }
        lhs = std::string(TrimWhitespace(lhs));
        const std::size_t close_bracket = lhs.rfind(']');
        if (close_bracket == std::string::npos || close_bracket + 1 != lhs.size() ||
            lhs.find('[') == std::string::npos) {
            continue;
        }
        const std::string next_trimmed =
            std::string(TrimWhitespace(StripLineComment(lines[line_index + 1])));
        if (!StartsWith(next_trimmed, lhs + ".")) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(line_index));
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_remove_indexed_table_assignment_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_remove_indexed_table_assignment",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactLoopAssignmentBreak(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("JMP") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 2 < lines.size(); ++line_index) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment(lines[line_index]));
        if (!StartsWith(if_trimmed, "if ") || if_trimmed.find(" then") == std::string_view::npos) {
            continue;
        }
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[line_index + 1], &is_local, &lhs, &rhs) ||
            is_local) {
            continue;
        }
        if (TrimWhitespace(StripLineComment(lines[line_index + 2])) != "end" ||
            TrimWhitespace(StripLineComment(lines[line_index + 1])) == "break") {
            continue;
        }
        if (line_index + 3 < lines.size() &&
            TrimWhitespace(StripLineComment(lines[line_index + 3])) == "break") {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.insert(transformed_lines.begin() +
                                     static_cast<std::ptrdiff_t>(line_index + 2),
                                 GetLuaLineIndent(lines[line_index + 1]) + "break");
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_loop_assignment_break_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_loop_assignment_break",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactLoopTempTableInsertShape(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 5) {
        return false;
    }

    const auto parse_entry =
        [](std::string_view entry, std::string* key_out, std::string* value_out) {
            const std::size_t eq = FindBytecodeRepairTopLevelToken(entry, " = ");
            if (key_out == nullptr || value_out == nullptr ||
                eq == std::string_view::npos || eq == 0 || eq + 3 >= entry.size()) {
                return false;
            }
            const std::string key = std::string(TrimWhitespace(entry.substr(0, eq)));
            const std::string value = std::string(TrimWhitespace(entry.substr(eq + 3)));
            if (!refii::lua::IsValidLuaIdentifier(key) || value.empty()) {
                return false;
            }
            *key_out = key;
            *value_out = value;
            return true;
        };

    for (std::size_t local_line = 0; local_line < lines.size(); ++local_line) {
        bool is_local = false;
        std::string local_name;
        std::string rhs;
        if (!ParseBytecodeRepairPlainAssignment(lines[local_line],
                                                &is_local,
                                                &local_name,
                                                &rhs) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(local_name)) {
            continue;
        }
        rhs = std::string(TrimWhitespace(rhs));
        if (rhs.size() < 3 || rhs.front() != '{' || rhs.back() != '}') {
            continue;
        }
        const std::vector<std::string> entries =
            SplitBytecodeRepairTopLevelArgs(rhs.substr(1, rhs.size() - 2));
        if (entries.empty() || entries.size() > 6) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        const std::string indent = GetLuaLineIndent(lines[local_line]);
        replacement_lines.push_back(indent + "local " + local_name + " = {}");
        bool failed = false;
        for (const std::string& entry : entries) {
            std::string key;
            std::string value;
            if (!parse_entry(entry, &key, &value)) {
                failed = true;
                break;
            }
            replacement_lines.push_back(indent + local_name + "." + key + " = " + value);
        }
        if (failed) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(transformed_lines.begin() +
                                static_cast<std::ptrdiff_t>(local_line));
        transformed_lines.insert(transformed_lines.begin() +
                                     static_cast<std::ptrdiff_t>(local_line),
                                 replacement_lines.begin(),
                                 replacement_lines.end());
        for (std::size_t scan = local_line + replacement_lines.size();
             scan + 1 < transformed_lines.size() &&
             scan < local_line + replacement_lines.size() + 8;
             ++scan) {
            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseBytecodeRepairPlainAssignment(transformed_lines[scan],
                                                    &assign_is_local,
                                                    &assign_lhs,
                                                    &assign_rhs) ||
                assign_is_local ||
                TrimWhitespace(assign_rhs) != local_name ||
                assign_lhs.find('[') == std::string::npos ||
                assign_lhs.find(']') == std::string::npos) {
                continue;
            }
            const std::string next_trimmed =
                std::string(TrimWhitespace(StripLineComment(transformed_lines[scan + 1])));
            if (StartsWith(next_trimmed, std::string(TrimWhitespace(assign_lhs)) + ".")) {
                transformed_lines.erase(transformed_lines.begin() +
                                        static_cast<std::ptrdiff_t>(scan));
            }
            break;
        }

        if (local_line >= 2) {
            for (std::size_t scan = local_line; scan > 1 && local_line - scan < 12; --scan) {
                const std::size_t candidate_assignment = scan - 1;
                bool assign_is_local = false;
                std::string assign_lhs;
                std::string assign_rhs;
                if (!ParseBytecodeRepairPlainAssignment(lines[candidate_assignment],
                                                        &assign_is_local,
                                                        &assign_lhs,
                                                        &assign_rhs) ||
                    assign_is_local ||
                TrimWhitespace(StripLineComment(lines[candidate_assignment + 1])) != "end") {
                    continue;
                }
                const std::ptrdiff_t insert_at =
                    static_cast<std::ptrdiff_t>(candidate_assignment + 1);
                if (insert_at >= 0 &&
                    static_cast<std::size_t>(insert_at) <= transformed_lines.size()) {
                    transformed_lines.insert(transformed_lines.begin() + insert_at,
                                             GetLuaLineIndent(lines[candidate_assignment]) +
                                                 "break");
                }
                break;
            }
        }

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_loop_temp_table_insert_shape_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_loop_temp_table_insert_shape",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactLoopCallBreak(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("JMP") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    for (std::size_t line_index = 1; line_index + 1 < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        if (trimmed.empty() ||
            trimmed.find('=') != std::string_view::npos ||
            trimmed.find('(') == std::string_view::npos ||
            trimmed.back() != ')' ||
            TrimWhitespace(StripLineComment(lines[line_index + 1])) != "end") {
            continue;
        }
        bool inside_if = false;
        for (std::size_t scan = line_index; scan > 0 && line_index - scan < 8; --scan) {
            const std::string_view probe = TrimWhitespace(StripLineComment(lines[scan - 1]));
            if (StartsWith(probe, "if ") && probe.find(" then") != std::string_view::npos) {
                inside_if = true;
                break;
            }
        }
        if (!inside_if) {
            continue;
        }
        std::vector<std::string> transformed_lines = lines;
        transformed_lines.insert(transformed_lines.begin() +
                                     static_cast<std::ptrdiff_t>(line_index + 1),
                                 GetLuaLineIndent(lines[line_index]) + "break");
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_loop_call_break_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_loop_call_break",
                candidate_source)) {
            return true;
        }
    }
    return false;
}

bool TryRepairNearExactRedundantPostFailureBreakGuard(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("JMP") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 12 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 7) {
        return false;
    }

    const auto parse_negative_call_guard =
        [](std::string_view line, std::string* expression_out) {
            if (expression_out == nullptr) {
                return false;
            }
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (!StartsWith(trimmed, "if not ")) {
                return false;
            }
            const std::size_t then_pos =
                FindBytecodeRepairTopLevelToken(trimmed, " then");
            if (then_pos == std::string_view::npos) {
                return false;
            }
            const std::string expression =
                StripBytecodeRepairWholeParens(trimmed.substr(7, then_pos - 7));
            if (!IsBytecodeRepairSimpleCallExpression(expression)) {
                return false;
            }
            *expression_out = expression;
            return true;
        };

    for (std::size_t first_if = 0; first_if + 6 < lines.size(); ++first_if) {
        std::string first_call;
        if (!parse_negative_call_guard(lines[first_if], &first_call)) {
            continue;
        }

        std::size_t first_end = first_if + 1;
        int depth = 1;
        for (; first_end < lines.size(); ++first_end) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment(lines[first_end]));
            if (StartsWith(trimmed, "if ")) {
                ++depth;
            } else if (trimmed == "end") {
                --depth;
                if (depth == 0) {
                    break;
                }
            }
        }
        if (first_end >= lines.size() || first_end + 3 >= lines.size()) {
            continue;
        }

        std::string second_call;
        const std::size_t second_if = first_end + 1;
        if (!parse_negative_call_guard(lines[second_if], &second_call) ||
            TrimWhitespace(StripLineComment(lines[second_if + 1])) != "break" ||
            TrimWhitespace(StripLineComment(lines[second_if + 2])) != "end" ||
            first_call.find(':') == std::string::npos ||
            second_call.find(':') == std::string::npos) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(second_if),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(second_if + 3));
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_redundant_post_failure_break_guard_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_redundant_post_failure_break_guard",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactReturnedTableTempLiteral(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 16 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 3) {
        return false;
    }

    const auto parse_field_write =
        [](std::string_view line,
           std::string_view table_name,
           std::string* key_out,
           std::string* value_out) {
            if (key_out == nullptr || value_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseBytecodeRepairPlainAssignment(line, &is_local, &lhs, &rhs) ||
                is_local || lhs.empty() || rhs.empty()) {
                return false;
            }

            const std::string table_prefix = std::string(table_name);
            std::string key;
            if (StartsWith(lhs, table_prefix + ".")) {
                key = lhs.substr(table_prefix.size() + 1);
                if (!refii::lua::IsValidLuaIdentifier(key)) {
                    return false;
                }
            } else if (StartsWith(lhs, table_prefix + "[")) {
                key = lhs.substr(table_prefix.size());
                if (key.size() < 3 || key.front() != '[' || key.back() != ']') {
                    return false;
                }
            } else {
                return false;
            }

            if (rhs.find(table_prefix) != std::string::npos) {
                return false;
            }
            *key_out = std::move(key);
            *value_out = std::move(rhs);
            return true;
        };

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t index = start; index < lines.size(); ++index) {
            if (!TrimWhitespace(StripLineComment(lines[index])).empty()) {
                return index;
            }
        }
        return lines.size();
    };

    for (std::size_t seed_line = 0; seed_line + 2 < lines.size(); ++seed_line) {
        if (context.mismatch.line_defined != 0 &&
            seed_line + 1 + 16 < context.mismatch.line_defined) {
            continue;
        }
        bool seed_is_local = false;
        std::string table_name;
        std::string seed_rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[seed_line],
                                                 &seed_is_local,
                                                 &table_name,
                                                 &seed_rhs) ||
            !seed_is_local ||
            !refii::lua::IsValidLuaIdentifier(table_name) ||
            TrimWhitespace(seed_rhs) != "{}") {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[seed_line]);
        std::vector<std::pair<std::string, std::string>> fields;
        std::size_t scan_line = seed_line + 1;
        while (scan_line < lines.size()) {
            if (TrimWhitespace(StripLineComment(lines[scan_line])).empty()) {
                ++scan_line;
                continue;
            }
            if (GetLuaLineIndent(lines[scan_line]) != indent) {
                break;
            }
            std::string key;
            std::string value;
            if (!parse_field_write(lines[scan_line], table_name, &key, &value)) {
                break;
            }
            fields.emplace_back(std::move(key), std::move(value));
            ++scan_line;
            if (fields.size() > 8) {
                break;
            }
        }
        if (fields.empty() || fields.size() > 8) {
            continue;
        }

        const std::size_t return_line = next_nonempty_line(scan_line);
        if (return_line >= lines.size() ||
            GetLuaLineIndent(lines[return_line]) != indent) {
            continue;
        }

        const std::string_view return_trimmed =
            TrimWhitespace(StripLineComment(lines[return_line]));
        if (!StartsWith(return_trimmed, "return ")) {
            continue;
        }
        std::vector<std::string> return_values =
            SplitBytecodeRepairTopLevelArgs(return_trimmed.substr(7));
        if (return_values.empty()) {
            continue;
        }

        bool replaced_return_value = false;
        std::string table_literal = "{";
        for (std::size_t field_index = 0; field_index < fields.size(); ++field_index) {
            if (field_index != 0) {
                table_literal += ", ";
            }
            table_literal += fields[field_index].first;
            table_literal += " = ";
            table_literal += fields[field_index].second;
        }
        table_literal += "}";

        for (std::string& return_value : return_values) {
            if (TrimWhitespace(return_value) == table_name) {
                return_value = table_literal;
                replaced_return_value = true;
            }
        }
        if (!replaced_return_value) {
            continue;
        }

        std::string rebuilt_return = indent + "return ";
        for (std::size_t value_index = 0; value_index < return_values.size(); ++value_index) {
            if (value_index != 0) {
                rebuilt_return += ", ";
            }
            rebuilt_return += return_values[value_index];
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[return_line] = std::move(rebuilt_return);
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(seed_line),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(scan_line));

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_table_temp_literal_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_returned_table_temp_literal",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactEqualityOperandSwap(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("EQ(") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (!StartsWith(trimmed, "if ") || trimmed.find(" then") == std::string_view::npos) {
            continue;
        }
        const std::size_t then_pos = FindBytecodeRepairTopLevelToken(trimmed, " then");
        if (then_pos == std::string_view::npos) {
            continue;
        }
        const std::string condition =
            StripBytecodeRepairWholeParens(trimmed.substr(3, then_pos - 3));
        const std::size_t eq_pos = FindBytecodeRepairTopLevelToken(condition, " == ");
        if (eq_pos == std::string_view::npos) {
            continue;
        }

        const std::string lhs =
            StripBytecodeRepairWholeParens(condition.substr(0, eq_pos));
        const std::string rhs =
            StripBytecodeRepairWholeParens(condition.substr(eq_pos + 4));
        if (lhs.empty() || rhs.empty()) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + "if " + rhs + " == " + lhs + " then";
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_equality_operand_swap",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactRelationalOperandSwap(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("LT(") == std::string::npos &&
        context.mismatch.note.find("LE(") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 6 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto find_relational_operator =
        [](std::string_view expression,
           std::size_t* op_pos,
           std::string* op,
           std::string* swapped_op) {
            if (op_pos == nullptr || op == nullptr || swapped_op == nullptr) {
                return false;
            }
            const std::array<std::pair<std::string_view, std::string_view>, 4> ops = {
                std::pair<std::string_view, std::string_view>{">=", "<="},
                std::pair<std::string_view, std::string_view>{"<=", ">="},
                std::pair<std::string_view, std::string_view>{">", "<"},
                std::pair<std::string_view, std::string_view>{"<", ">"},
            };
            for (const auto& entry : ops) {
                const std::string token = " " + std::string(entry.first) + " ";
                const std::size_t pos = FindBytecodeRepairTopLevelToken(expression, token);
                if (pos == std::string_view::npos) {
                    continue;
                }
                *op_pos = pos;
                *op = std::string(entry.first);
                *swapped_op = std::string(entry.second);
                return true;
            }
            return false;
        };

    const auto try_swap_expression =
        [&](std::string_view expression, std::string* swapped_expression) {
            if (swapped_expression == nullptr) {
                return false;
            }
            const std::string stripped_expression =
                StripBytecodeRepairWholeParens(expression);
            std::size_t op_pos = std::string_view::npos;
            std::string op;
            std::string swapped_op;
            if (!find_relational_operator(stripped_expression,
                                          &op_pos,
                                          &op,
                                          &swapped_op)) {
                return false;
            }
            const std::string lhs =
                StripBytecodeRepairWholeParens(stripped_expression.substr(0, op_pos));
            const std::string rhs = StripBytecodeRepairWholeParens(
                stripped_expression.substr(op_pos + op.size() + 2));
            if (lhs.empty() || rhs.empty() ||
                lhs.find(',') != std::string::npos ||
                rhs.find(',') != std::string::npos) {
                return false;
            }
            *swapped_expression = rhs + " " + swapped_op + " " + lhs;
            return true;
        };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        std::string swapped_expression;
        std::string rebuilt_line;

        if (StartsWith(trimmed, "return ")) {
            const std::string expression =
                std::string(TrimWhitespace(trimmed.substr(7)));
            if (!try_swap_expression(expression, &swapped_expression)) {
                continue;
            }
            rebuilt_line =
                GetLuaLineIndent(lines[line_index]) + "return " + swapped_expression;
        } else if (StartsWith(trimmed, "if ") &&
                   trimmed.find(" then") != std::string_view::npos) {
            const std::size_t then_pos =
                FindBytecodeRepairTopLevelToken(trimmed, " then");
            if (then_pos == std::string_view::npos) {
                continue;
            }
            const std::string condition =
                std::string(TrimWhitespace(trimmed.substr(3, then_pos - 3)));
            if (!try_swap_expression(condition, &swapped_expression)) {
                continue;
            }
            rebuilt_line =
                GetLuaLineIndent(lines[line_index]) + "if " + swapped_expression + " then";
        } else {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] = rebuilt_line;
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_relational_operand_swap",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_relational_operand_swap",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactRelationalLocalCallOperandInlineSwap(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("GETTABLE") == std::string::npos &&
        context.mismatch.note.find("LT(") == std::string::npos &&
        context.mismatch.note.find("LE(") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    const auto find_relational_operator =
        [](std::string_view expression,
           std::size_t* op_pos,
           std::string* op,
           std::string* swapped_op) {
            if (op_pos == nullptr || op == nullptr || swapped_op == nullptr) {
                return false;
            }
            const std::array<std::pair<std::string_view, std::string_view>, 4> ops = {
                std::pair<std::string_view, std::string_view>{">=", "<="},
                std::pair<std::string_view, std::string_view>{"<=", ">="},
                std::pair<std::string_view, std::string_view>{">", "<"},
                std::pair<std::string_view, std::string_view>{"<", ">"},
            };
            for (const auto& entry : ops) {
                const std::string token = " " + std::string(entry.first) + " ";
                const std::size_t pos = FindBytecodeRepairTopLevelToken(expression, token);
                if (pos == std::string_view::npos) {
                    continue;
                }
                *op_pos = pos;
                *op = std::string(entry.first);
                *swapped_op = std::string(entry.second);
                return true;
            }
            return false;
        };

    for (std::size_t local_line = 0; local_line + 1 < lines.size(); ++local_line) {
        bool is_local = false;
        std::string local_name;
        std::string initializer;
        if (!ParseBytecodeRepairSingleAssignment(lines[local_line],
                                                 &is_local,
                                                 &local_name,
                                                 &initializer) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(local_name) ||
            !IsBytecodeRepairSimpleCallExpression(initializer)) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimWhitespace(StripLineComment(lines[local_line + 1]));
        if (!StartsWith(trimmed_if, "if ")) {
            continue;
        }
        const std::size_t then_pos =
            FindBytecodeRepairTopLevelToken(trimmed_if, " then");
        if (then_pos == std::string_view::npos) {
            continue;
        }
        const std::string condition =
            StripBytecodeRepairWholeParens(trimmed_if.substr(3, then_pos - 3));
        std::size_t op_pos = std::string_view::npos;
        std::string op;
        std::string swapped_op;
        if (!find_relational_operator(condition, &op_pos, &op, &swapped_op)) {
            continue;
        }

        const std::string lhs =
            StripBytecodeRepairWholeParens(condition.substr(0, op_pos));
        const std::string rhs =
            StripBytecodeRepairWholeParens(condition.substr(op_pos + op.size() + 2));
        if (lhs.empty() || rhs.empty()) {
            continue;
        }

        std::string rebuilt_condition;
        if (rhs == local_name) {
            rebuilt_condition = StripBytecodeRepairWholeParens(initializer) + " " +
                                swapped_op + " " + lhs;
        } else if (lhs == local_name) {
            rebuilt_condition = rhs + " " + swapped_op + " " +
                                StripBytecodeRepairWholeParens(initializer);
        } else {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[local_line + 1] =
            GetLuaLineIndent(lines[local_line + 1]) + "if " +
            rebuilt_condition + " then";
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(local_line));
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_relational_local_call_inline_swap",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_relational_local_call_inline_swap",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactPairedNegativeGuardCallCarry(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("LOADBOOL") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 2 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    const refii::lua::Lua51Function* original_function =
        GetLuaFunctionByPreorderIndex(context.original_chunk,
                                      context.mismatch.function_index);
    if (original_function == nullptr) {
        return false;
    }
    std::string carry_name;
    for (const refii::lua::Lua51Local& local : original_function->locals) {
        if (local.name != "self" && refii::lua::IsValidLuaIdentifier(local.name)) {
            carry_name = local.name;
            break;
        }
    }
    if (carry_name.empty()) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 7) {
        return false;
    }

    const auto parse_negative_call_guard =
        [](std::string_view line, std::string* call_expression) {
            if (call_expression == nullptr) {
                return false;
            }
            const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
            if (!StartsWith(trimmed, "if not ")) {
                return false;
            }
            const std::size_t then_pos =
                FindBytecodeRepairTopLevelToken(trimmed, " then");
            if (then_pos == std::string_view::npos) {
                return false;
            }
            const std::string expression =
                StripBytecodeRepairWholeParens(trimmed.substr(7, then_pos - 7));
            if (!IsBytecodeRepairSimpleCallExpression(expression)) {
                return false;
            }
            *call_expression = expression;
            return true;
        };
    const auto is_return_false = [](std::string_view line) {
        return TrimWhitespace(StripLineComment(line)) == "return false";
    };
    const auto is_end = [](std::string_view line) {
        return TrimWhitespace(StripLineComment(line)) == "end";
    };

    for (std::size_t first_if = 0; first_if + 6 < lines.size(); ++first_if) {
        std::string first_call;
        if (!parse_negative_call_guard(lines[first_if], &first_call) ||
            !is_return_false(lines[first_if + 1]) ||
            !is_end(lines[first_if + 2])) {
            continue;
        }
        if (first_call.find(carry_name) != std::string::npos) {
            continue;
        }

        const std::size_t scan_end =
            std::min<std::size_t>(lines.size(), first_if + 10);
        for (std::size_t local_line = first_if + 3;
             local_line + 3 < scan_end;
             ++local_line) {
            bool is_local = false;
            std::string local_name;
            std::string initializer;
            if (!ParseBytecodeRepairSingleAssignment(lines[local_line],
                                                     &is_local,
                                                     &local_name,
                                                     &initializer) ||
                !is_local ||
                !refii::lua::IsValidLuaIdentifier(local_name) ||
                !IsBytecodeRepairSimpleCallExpression(initializer)) {
                continue;
            }
            const std::string_view second_if_trimmed =
                TrimWhitespace(StripLineComment(lines[local_line + 1]));
            if (second_if_trimmed != "if not " + local_name + " then" ||
                !is_return_false(lines[local_line + 2]) ||
                !is_end(lines[local_line + 3])) {
                continue;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines[first_if] =
                GetLuaLineIndent(lines[first_if]) + "if not " + carry_name + " then";
            transformed_lines[local_line + 1] =
                GetLuaLineIndent(lines[local_line + 1]) + "if not " +
                StripBytecodeRepairWholeParens(initializer) + " then";
            transformed_lines.insert(
                transformed_lines.begin() + static_cast<std::ptrdiff_t>(first_if),
                GetLuaLineIndent(lines[first_if]) + "local " + carry_name +
                    " = " + first_call);
            transformed_lines.erase(
                transformed_lines.begin() +
                static_cast<std::ptrdiff_t>(local_line + 1));

            if (TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_paired_negative_guard_call_carry",
                    candidate_source)) {
                return true;
            }
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_paired_negative_guard_call_carry",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactNestedCallArgumentLocal(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 1 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty() || StartsWith(trimmed, "if ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "return ") ||
            StartsWith(trimmed, "local ") || trimmed.find('=') != std::string_view::npos ||
            !EndsWith(trimmed, ")")) {
            continue;
        }

        const std::size_t outer_open = trimmed.find('(');
        if (outer_open == std::string_view::npos || outer_open == 0 ||
            trimmed.back() != ')') {
            continue;
        }
        const std::string outer_callee =
            std::string(TrimWhitespace(trimmed.substr(0, outer_open)));
        if (!IsBytecodeRepairSimpleCalleePath(outer_callee)) {
            continue;
        }
        const std::string args_body =
            std::string(trimmed.substr(outer_open + 1,
                                       trimmed.size() - outer_open - 2));
        std::vector<std::string> args = SplitBytecodeRepairTopLevelArgs(args_body);
        if (args.size() < 2) {
            continue;
        }

        std::size_t nested_arg_index = args.size() - 1;
        std::string nested = StripBytecodeRepairWholeParens(args[nested_arg_index]);
        const std::size_t nested_open = nested.find('(');
        if (nested_open == std::string::npos || nested_open == 0 ||
            nested.back() != ')' ||
            nested.find(',', nested_open + 1) == std::string::npos) {
            continue;
        }
        const std::string nested_callee =
            std::string(TrimWhitespace(std::string_view(nested).substr(0, nested_open)));
        if (!IsBytecodeRepairSimpleCalleePath(nested_callee)) {
            continue;
        }

        const std::string temp_name =
            ChooseBytecodeRepairTempName(lines, nested, false);
        args[nested_arg_index] = temp_name;
        std::string rebuilt_args;
        for (std::size_t arg_index = 0; arg_index < args.size(); ++arg_index) {
            if (arg_index != 0) {
                rebuilt_args += ", ";
            }
            rebuilt_args += args[arg_index];
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            indent + outer_callee + "(" + rebuilt_args + ")";
        transformed_lines.insert(transformed_lines.begin() +
                                     static_cast<std::ptrdiff_t>(line_index),
                                 indent + "local " + temp_name + " = " + nested);

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_nested_call_argument_local",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_nested_call_argument_local",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactCallArgumentLocalOrderSwap(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    for (std::size_t local_line = 0; local_line + 1 < lines.size(); ++local_line) {
        bool is_local = false;
        std::string local_name;
        std::string initializer;
        if (!ParseBytecodeRepairSingleAssignment(lines[local_line],
                                                 &is_local,
                                                 &local_name,
                                                 &initializer) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(local_name) ||
            !IsBytecodeRepairSimpleCallExpression(initializer)) {
            continue;
        }

        const std::string_view stripped = StripLineComment(lines[local_line + 1]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty() || StartsWith(trimmed, "if ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "return ") ||
            StartsWith(trimmed, "local ") || trimmed.find('=') != std::string_view::npos ||
            !EndsWith(trimmed, ")")) {
            continue;
        }

        const std::size_t outer_open = trimmed.find('(');
        if (outer_open == std::string_view::npos || outer_open == 0 ||
            trimmed.back() != ')') {
            continue;
        }
        const std::string outer_callee =
            std::string(TrimWhitespace(trimmed.substr(0, outer_open)));
        if (!IsBytecodeRepairSimpleCalleePath(outer_callee)) {
            continue;
        }

        const std::string args_body =
            std::string(trimmed.substr(outer_open + 1,
                                       trimmed.size() - outer_open - 2));
        std::vector<std::string> args = SplitBytecodeRepairTopLevelArgs(args_body);
        if (args.size() < 2 || TrimWhitespace(args[1]) != local_name) {
            continue;
        }

        const std::string first_arg = StripBytecodeRepairWholeParens(args[0]);
        if (!IsBytecodeRepairSimpleCallExpression(first_arg)) {
            continue;
        }

        args[0] = local_name;
        args[1] = StripBytecodeRepairWholeParens(initializer);
        std::string rebuilt_args;
        for (std::size_t arg_index = 0; arg_index < args.size(); ++arg_index) {
            if (arg_index != 0) {
                rebuilt_args += ", ";
            }
            rebuilt_args += args[arg_index];
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[local_line] =
            GetLuaLineIndent(lines[local_line]) + "local " + local_name + " = " + first_arg;
        transformed_lines[local_line + 1] =
            GetLuaLineIndent(lines[local_line + 1]) +
            outer_callee + "(" + rebuilt_args + ")";

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_call_argument_local_order_swap",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_call_argument_local_order_swap",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactManagerStateLiteralAndCachedUiLocals(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("NEWTABLE") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 16 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 6) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const auto trim_string = [](const std::string& line) {
        return std::string(TrimWhitespace(StripLineComment(line)));
    };

    const auto replace_all = [](std::string* text,
                                std::string_view needle,
                                std::string_view replacement) {
        if (text == nullptr || needle.empty()) {
            return false;
        }
        bool changed = false;
        std::size_t pos = 0;
        while ((pos = text->find(needle, pos)) != std::string::npos) {
            text->replace(pos, needle.size(), replacement);
            pos += replacement.size();
            changed = true;
        }
        return changed;
    };

    bool changed = false;

    for (std::size_t line_index = 0; line_index + 6 < lines.size(); ++line_index) {
        bool is_local = false;
        std::string alias_name;
        std::string receiver;
        if (!ParseBytecodeRepairSingleAssignment(lines[line_index],
                                                 &is_local,
                                                 &alias_name,
                                                 &receiver) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(alias_name) ||
            !refii::lua::IsValidLuaIdentifier(receiver)) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        bool same_indent = true;
        for (std::size_t offset = 1; offset <= 6; ++offset) {
            if (GetLuaLineIndent(lines[line_index + offset]) != indent) {
                same_indent = false;
                break;
            }
        }
        if (!same_indent) {
            continue;
        }

        const std::string alias_prefix = alias_name + ".";
        const std::string image_line =
            alias_prefix + "Image = AK.fetchObjects(" + alias_name + ", \"Image\")";
        const std::string name_line =
            alias_prefix + "Name = AK.fetchObjects(" + alias_name + ", \"Name\")";
        const std::string description_line =
            alias_prefix + "Description = AK.fetchObjects(" + alias_name +
            ", \"Description\")";
        const std::string description_component_line =
            alias_prefix + "DescriptionComponent = AK.fetchObjects(" + alias_name +
            ", \"DescriptionComponent\")";
        const std::string last_update_line =
            alias_prefix + "LastUpdateTime = getElapsedTime()";
        const std::string state_alias_line =
            alias_prefix + "State = " + alias_name;
        if (trim_string(lines[line_index + 1]) != image_line ||
            trim_string(lines[line_index + 2]) != name_line ||
            trim_string(lines[line_index + 3]) != description_line ||
            trim_string(lines[line_index + 4]) != description_component_line ||
            trim_string(lines[line_index + 5]) != last_update_line ||
            trim_string(lines[line_index + 6]) != state_alias_line) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        replacement_lines.push_back(indent + receiver + ".State = {");
        replacement_lines.push_back(indent + "  Image = AK.fetchObjects(" +
                                    receiver + ", \"Image\"),");
        replacement_lines.push_back(indent + "  Name = AK.fetchObjects(" +
                                    receiver + ", \"Name\"),");
        replacement_lines.push_back(indent + "  Description = AK.fetchObjects(" +
                                    receiver + ", \"Description\"),");
        replacement_lines.push_back(
            indent + "  DescriptionComponent = AK.fetchObjects(" + receiver +
            ", \"DescriptionComponent\"),");
        replacement_lines.push_back(indent + "  LastUpdateTime = getElapsedTime()");
        replacement_lines.push_back(indent + "}");
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(line_index + 7));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement_lines.begin(),
                     replacement_lines.end());

        for (std::size_t probe = line_index + replacement_lines.size();
             probe < lines.size();
             ++probe) {
            const std::string probe_indent = GetLuaLineIndent(lines[probe]);
            const std::string probe_trimmed = trim_string(lines[probe]);
            if (probe_indent == indent && probe_trimmed == "end") {
                break;
            }
            if (probe_indent != indent ||
                !probe_trimmed.starts_with(alias_prefix)) {
                continue;
            }
            lines[probe] = probe_indent + receiver +
                           probe_trimmed.substr(alias_name.size());
        }
        changed = true;
        break;
    }

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        const std::string indent = GetLuaLineIndent(lines[line_index]);
        const std::string indexed_menu_item =
            "g_Menu.CurrWheelNode.ChildMenuItems[g_Menu.CurrWheelNode.HighlightedIdx]";
        if (trim_string(lines[line_index]) !=
                "local L2_2 = A0_0.State.DescriptionComponent.slide" ||
            GetLuaLineIndent(lines[line_index + 1]) != indent ||
            trim_string(lines[line_index + 1]) !=
                "if " + indexed_menu_item + " then") {
            continue;
        }

        lines[line_index] =
            indent + "local L1_1 = " + indexed_menu_item;
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     indent + "local L2_2 = A0_0.State.DescriptionComponent.slide");
        lines[line_index + 2] = indent + "if L1_1 then";

        const std::string time_context_expression =
            "A0_0.State.DescriptionComponent:getTimeContext()";
        bool inserted_time_local = false;
        for (std::size_t probe = line_index + 3; probe < lines.size(); ++probe) {
            const std::string probe_trimmed = trim_string(lines[probe]);
            if (probe_trimmed == "end" && GetLuaLineIndent(lines[probe]) == indent) {
                break;
            }
            if (probe_trimmed ==
                "if g_Menu.Const.MENU_ITEM_TYPE_MENU_BUTTON == L1_1.MenuItemType then") {
                lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(probe),
                             indent + "local L3_3 = " + time_context_expression);
                inserted_time_local = true;
                break;
            }
        }
        if (!inserted_time_local) {
            continue;
        }

        for (std::size_t probe = line_index + 3; probe < lines.size(); ++probe) {
            const std::string probe_trimmed = trim_string(lines[probe]);
            if (probe_trimmed == "end" && GetLuaLineIndent(lines[probe]) == indent) {
                break;
            }
            replace_all(&lines[probe], time_context_expression + ".time", "L3_3.time");
            replace_all(&lines[probe], time_context_expression + ".paused", "L3_3.paused");
            replace_all(&lines[probe], time_context_expression + ":play()", "L3_3:play()");
        }

        for (std::size_t probe = line_index + 3; probe + 3 < lines.size(); ++probe) {
            if (GetLuaLineIndent(lines[probe]) != indent ||
                trim_string(lines[probe]) != "if 2 == L2_2 then") {
                continue;
            }
            if (trim_string(lines[probe + 2]) == "end" &&
                trim_string(lines[probe + 3]) == "if L3_3.time == 0 then") {
                lines[probe + 2] = GetLuaLineIndent(lines[probe + 2]) +
                                   "elseif L3_3.time == 0 then";
                lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(probe + 3));
                break;
            }
        }
        changed = true;
        break;
    }

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool is_local = false;
        std::string slide_local;
        std::string slide_rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[line_index],
                                                 &is_local,
                                                 &slide_local,
                                                 &slide_rhs) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(slide_local) ||
            !EndsWith(TrimWhitespace(slide_rhs),
                      ".State.DescriptionComponent.slide")) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        if (GetLuaLineIndent(lines[line_index + 1]) != indent) {
            continue;
        }
        const std::string condition_line = trim_string(lines[line_index + 1]);
        const std::string indexed_menu_item =
            "g_Menu.CurrWheelNode.ChildMenuItems[g_Menu.CurrWheelNode.HighlightedIdx]";
        if (condition_line != "if " + indexed_menu_item + " then") {
            continue;
        }

        const std::string receiver =
            std::string(TrimWhitespace(slide_rhs.substr(
                0,
                slide_rhs.size() -
                    std::string(".State.DescriptionComponent.slide").size())));
        if (!refii::lua::IsValidLuaIdentifier(receiver)) {
            continue;
        }

        std::string item_local = "L1_1";
        for (std::size_t probe = line_index + 2; probe < lines.size(); ++probe) {
            const std::string probe_trimmed = trim_string(lines[probe]);
            if (probe_trimmed == "end" && GetLuaLineIndent(lines[probe]) == indent) {
                break;
            }
            const std::string marker = " == ";
            const std::size_t marker_pos = probe_trimmed.find(marker);
            const std::string suffix = ".MenuItemType then";
            if (marker_pos != std::string::npos && EndsWith(probe_trimmed, suffix)) {
                const std::size_t start = marker_pos + marker.size();
                item_local = probe_trimmed.substr(
                    start,
                    probe_trimmed.size() - start - suffix.size());
                break;
            }
        }
        if (!refii::lua::IsValidLuaIdentifier(item_local)) {
            continue;
        }

        lines[line_index] =
            indent + "local " + item_local + " = " + indexed_menu_item;
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     indent + "local " + slide_local + " = " + slide_rhs);
        lines[line_index + 2] = indent + "if " + item_local + " then";

        const std::string time_context_expression =
            receiver + ".State.DescriptionComponent:getTimeContext()";
        std::string time_local = "L3_3";
        bool inserted_time_local = false;
        for (std::size_t probe = line_index + 3; probe < lines.size(); ++probe) {
            const std::string probe_trimmed = trim_string(lines[probe]);
            if (probe_trimmed == "end" && GetLuaLineIndent(lines[probe]) == indent) {
                break;
            }
            if (probe_trimmed == "if g_Menu.Const.MENU_ITEM_TYPE_MENU_BUTTON == " +
                                     item_local + ".MenuItemType then") {
                lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(probe),
                             indent + "local " + time_local + " = " +
                                 time_context_expression);
                inserted_time_local = true;
                break;
            }
        }
        if (!inserted_time_local) {
            continue;
        }

        for (std::size_t probe = line_index + 3; probe < lines.size(); ++probe) {
            const std::string probe_trimmed = trim_string(lines[probe]);
            if (probe_trimmed == "end" && GetLuaLineIndent(lines[probe]) == indent) {
                break;
            }
            replace_all(&lines[probe],
                        time_context_expression + ".time",
                        time_local + ".time");
            replace_all(&lines[probe],
                        time_context_expression + ".paused",
                        time_local + ".paused");
            replace_all(&lines[probe],
                        time_context_expression + ":play()",
                        time_local + ":play()");
        }

        for (std::size_t probe = line_index + 3; probe + 2 < lines.size(); ++probe) {
            if (GetLuaLineIndent(lines[probe]) != indent ||
                trim_string(lines[probe]) != "if 2 == " + slide_local + " then") {
                continue;
            }
            const std::string end_trimmed = trim_string(lines[probe + 2]);
            const std::string next_trimmed = trim_string(lines[probe + 3]);
            if (end_trimmed == "end" &&
                next_trimmed == "if " + time_local + ".time == 0 then") {
                lines[probe + 2] =
                    GetLuaLineIndent(lines[probe + 2]) + "elseif " + time_local +
                    ".time == 0 then";
                lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(probe + 3));
                break;
            }
        }
        changed = true;
        break;
    }

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        const std::string indent = GetLuaLineIndent(lines[line_index]);
        const std::string first_trimmed = trim_string(lines[line_index]);
        const std::string second_trimmed = trim_string(lines[line_index + 1]);
        const std::string chain =
            "A0_0.State.Image:getChildByIndex((1)):getChildByIndex((1))";
        if (first_trimmed != chain + ".positionu = A1_1" ||
            second_trimmed != chain + ".positionv = A2_2" ||
            GetLuaLineIndent(lines[line_index + 1]) != indent) {
            continue;
        }
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     indent + "local L3_3 = " + chain);
        lines[line_index + 1] = indent + "L3_3.positionu = A1_1";
        lines[line_index + 2] = indent + "L3_3.positionv = A2_2";
        changed = true;
        break;
    }

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool first_is_local = false;
        bool second_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        std::string second_lhs;
        std::string second_rhs;
        if (!ParseBytecodeRepairSingleAssignment(lines[line_index],
                                                 &first_is_local,
                                                 &first_lhs,
                                                 &first_rhs) ||
            !ParseBytecodeRepairSingleAssignment(lines[line_index + 1],
                                                 &second_is_local,
                                                 &second_lhs,
                                                 &second_rhs) ||
            first_is_local || second_is_local ||
            !EndsWith(first_lhs, ".positionu") ||
            !EndsWith(second_lhs, ".positionv") ||
            first_rhs.empty() || second_rhs.empty()) {
            continue;
        }

        const std::string first_base =
            first_lhs.substr(0, first_lhs.size() - std::string(".positionu").size());
        const std::string second_base =
            second_lhs.substr(0, second_lhs.size() - std::string(".positionv").size());
        if (first_base != second_base ||
            first_base.find(":getChildByIndex") == std::string::npos) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        if (GetLuaLineIndent(lines[line_index + 1]) != indent) {
            continue;
        }

        const std::string child_local = "L3_3";
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     indent + "local " + child_local + " = " + first_base);
        lines[line_index + 1] =
            indent + child_local + ".positionu = " + first_rhs;
        lines[line_index + 2] =
            indent + child_local + ".positionv = " + second_rhs;
        changed = true;
        break;
    }

    if (!changed) {
        return false;
    }

    if (TryCommitBytecodeParityExactCurrentFunctionLines(
            context,
            lines,
            had_trailing_newline,
            "bytecode_parity_candidate_manager_state_literal_cached_ui_locals",
            candidate_source)) {
        return true;
    }
    if (TryCommitBytecodeParityImprovingLines(
            context,
            lines,
            had_trailing_newline,
            "bytecode_parity_candidate_manager_state_literal_cached_ui_locals",
            candidate_source)) {
        return true;
    }

    return false;
}

bool TryRepairNearExactRepeatedCallArgumentLocal(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos &&
        context.mismatch.note.find("local_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    const auto normalize_expression = [](std::string_view expression) {
        expression = TrimWhitespace(StripBytecodeRepairWholeParens(expression));
        std::string normalized;
        normalized.reserve(expression.size());
        char quote = '\0';
        bool escaped = false;
        for (const char ch : expression) {
            if (quote != '\0') {
                normalized.push_back(ch);
                if (escaped) {
                    escaped = false;
                } else if (ch == '\\') {
                    escaped = true;
                } else if (ch == quote) {
                    quote = '\0';
                }
                continue;
            }
            if (ch == '\'' || ch == '"') {
                quote = ch;
                normalized.push_back(ch);
                continue;
            }
            if (!std::isspace(static_cast<unsigned char>(ch))) {
                normalized.push_back(ch);
            }
        }
        return quote == '\0' ? normalized : std::string();
    };

    const auto rebuild_call = [](std::string_view indent,
                                 const std::string& callee,
                                 const std::vector<std::string>& args) {
        std::string rebuilt = std::string(indent) + callee + "(";
        for (std::size_t arg_index = 0; arg_index < args.size(); ++arg_index) {
            if (arg_index != 0) {
                rebuilt += ", ";
            }
            rebuilt += args[arg_index];
        }
        rebuilt += ")";
        return rebuilt;
    };

    for (std::size_t first_line = 0; first_line + 1 < lines.size(); ++first_line) {
        const std::string_view first_trimmed =
            TrimWhitespace(StripLineComment(lines[first_line]));
        const std::string_view second_trimmed =
            TrimWhitespace(StripLineComment(lines[first_line + 1]));
        if (first_trimmed.empty() || second_trimmed.empty() ||
            first_trimmed.find('=') != std::string_view::npos ||
            second_trimmed.find('=') != std::string_view::npos ||
            StartsWith(first_trimmed, "return ") ||
            StartsWith(second_trimmed, "return ") ||
            StartsWith(first_trimmed, "if ") ||
            StartsWith(second_trimmed, "if ") ||
            GetLuaLineIndent(lines[first_line]) !=
                GetLuaLineIndent(lines[first_line + 1])) {
            continue;
        }

        std::string first_callee;
        std::vector<std::string> first_args;
        std::string second_callee;
        std::vector<std::string> second_args;
        if (!ParseRenderedSingleCallLine(first_trimmed, &first_callee, &first_args) ||
            !ParseRenderedSingleCallLine(second_trimmed, &second_callee, &second_args) ||
            first_callee != second_callee ||
            !IsBytecodeRepairSimpleCalleePath(first_callee) ||
            first_args.size() < 2 ||
            first_args.size() != second_args.size()) {
            continue;
        }

        for (std::size_t arg_index = 0; arg_index < first_args.size(); ++arg_index) {
            const std::string first_arg =
                StripBytecodeRepairWholeParens(first_args[arg_index]);
            const std::string second_arg =
                StripBytecodeRepairWholeParens(second_args[arg_index]);
            if (!IsBytecodeRepairSimpleCallExpression(first_arg) ||
                normalize_expression(first_arg) != normalize_expression(second_arg)) {
                continue;
            }

            const std::string temp_name =
                ChooseBytecodeRepairTempName(lines, first_arg, false);
            std::vector<std::string> transformed_lines = lines;
            std::vector<std::string> rewritten_first_args = first_args;
            std::vector<std::string> rewritten_second_args = second_args;
            rewritten_first_args[arg_index] = temp_name;
            rewritten_second_args[arg_index] = temp_name;

            const std::string indent = GetLuaLineIndent(lines[first_line]);
            transformed_lines[first_line] =
                rebuild_call(indent, first_callee, rewritten_first_args);
            transformed_lines[first_line + 1] =
                rebuild_call(indent, second_callee, rewritten_second_args);
            transformed_lines.insert(
                transformed_lines.begin() +
                    static_cast<std::ptrdiff_t>(first_line),
                indent + "local " + temp_name + " = " + first_arg);

            if (TryCommitBytecodeParityExactCurrentFunctionLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_repeated_call_argument_local",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactMismatchedStringLiteralFromParityNote(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source);

bool TryRepairNearExactReturnCallBooleanGuard(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (!StartsWith(trimmed, "return ")) {
            continue;
        }
        const std::string expression =
            StripBytecodeRepairWholeParens(trimmed.substr(7));
        if (!IsBytecodeRepairSimpleCallExpression(expression)) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        const std::string temp_name =
            ChooseBytecodeRepairTempName(lines, expression, true);

        std::vector<std::string> direct_guard_lines = lines;
        direct_guard_lines[line_index] = indent + "if " + expression + " then";
        direct_guard_lines.insert(
            direct_guard_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1),
            indent + "  return true");
        direct_guard_lines.insert(
            direct_guard_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 2),
            indent + "end");
        direct_guard_lines.insert(
            direct_guard_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 3),
            indent + "return false");

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                direct_guard_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_return_call_boolean_direct_guard",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                direct_guard_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_return_call_boolean_direct_guard",
                candidate_source)) {
            return true;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] = indent + "local " + temp_name + " = " + expression;
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1),
            indent + "if not " + temp_name + " then");
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 2),
            indent + "  return false");
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 3),
            indent + "end");
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 4),
            indent + "return true");

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_return_call_boolean_guard",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_return_call_boolean_guard",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactWrapReturnedCallForNonTailCall(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("TAILCALL") == std::string::npos &&
        context.mismatch.note.find("CALL") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment(lines[line_index]));
        if (!StartsWith(trimmed, "return ")) {
            continue;
        }

        const std::string expression =
            std::string(TrimWhitespace(trimmed.substr(std::string_view("return ").size())));
        if (expression.empty() ||
            (expression.front() == '(' && expression.back() == ')') ||
            !IsBytecodeRepairSimpleCallExpression(expression)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + "return (" + expression + ")";
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_wrap_returned_call_for_non_tailcall_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_wrap_returned_call_for_non_tailcall",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactInlineLocalCallBooleanReturnGuard(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 5) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t local_line = 0; local_line < lines.size(); ++local_line) {
        bool is_local = false;
        std::string local_name;
        std::string initializer;
        if (!ParseBytecodeRepairSingleAssignment(lines[local_line],
                                                 &is_local,
                                                 &local_name,
                                                 &initializer) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(local_name) ||
            !IsBytecodeRepairSimpleCallExpression(initializer)) {
            continue;
        }

        const std::size_t if_line = next_nonempty_line(local_line + 1);
        const std::size_t true_line = next_nonempty_line(if_line + 1);
        const std::size_t end_line = next_nonempty_line(true_line + 1);
        const std::size_t false_line = next_nonempty_line(end_line + 1);
        if (false_line >= lines.size()) {
            continue;
        }

        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment(lines[if_line]));
        const std::string_view true_trimmed =
            TrimWhitespace(StripLineComment(lines[true_line]));
        const std::string_view end_trimmed =
            TrimWhitespace(StripLineComment(lines[end_line]));
        const std::string_view false_trimmed =
            TrimWhitespace(StripLineComment(lines[false_line]));
        if (if_trimmed != "if " + local_name + " then" ||
            true_trimmed != "return true" ||
            end_trimmed != "end" ||
            false_trimmed != "return false") {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[if_line] =
            GetLuaLineIndent(lines[if_line]) + "if " + initializer + " then";
        transformed_lines.erase(transformed_lines.begin() +
                                    static_cast<std::ptrdiff_t>(local_line));

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_inline_local_call_boolean_return_guard_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_inline_local_call_boolean_return_guard",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactRemoveBreakGuardBeforeYield(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("JMP") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 3 < lines.size(); ++line_index) {
        std::string indent;
        std::string condition;
        if (!TryExtractBytecodeRepairIfCondition(lines[line_index], &indent, &condition) ||
            condition.empty()) {
            continue;
        }
        const std::string_view break_line =
            TrimWhitespace(StripLineComment(lines[line_index + 1]));
        const std::string_view end_line =
            TrimWhitespace(StripLineComment(lines[line_index + 2]));
        const std::string_view yield_line =
            TrimWhitespace(StripLineComment(lines[line_index + 3]));
        if (break_line != "break" || end_line != "end" ||
            yield_line != "coroutine.yield()") {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines.erase(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index),
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_remove_break_guard_before_yield_exact",
                candidate_source)) {
            return true;
        }
        if (TryCommitBytecodeParityImprovingLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_remove_break_guard_before_yield",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactTopLevelTableSeedFieldsForCurrentFunction(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("NEWTABLE") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 32 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 2) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    auto try_transform = [&](bool (*transform)(std::vector<std::string>*),
                             std::string_view label) {
        std::vector<std::string> transformed_lines = lines;
        if (!transform(&transformed_lines) || transformed_lines == lines) {
            return false;
        }
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                label,
                candidate_source)) {
            return true;
        }
        return TryCommitBytecodeParityImprovingLines(context,
                                                     transformed_lines,
                                                     had_trailing_newline,
                                                     label,
                                                     candidate_source);
    };

    if (try_transform(refii::lua::ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity,
                      "bytecode_parity_candidate_inline_top_level_table_seed_fields_current")) {
        return true;
    }
    if (try_transform(
            refii::lua::ExpandRenderedLuaMultilineTopLevelTableSeedFieldsForParity,
            "bytecode_parity_candidate_multiline_top_level_table_seed_fields_current")) {
        return true;
    }
    return false;
}

bool TryRepairNearExactLocalCallReturnBooleanGuard(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 2 ||
        std::llabs(context.mismatch.local_delta) > 3) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    const auto next_nonempty_line = [&](std::size_t start) {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t local_line = 0; local_line < lines.size(); ++local_line) {
        bool is_local = false;
        std::string local_name;
        std::string initializer;
        if (!ParseBytecodeRepairSingleAssignment(lines[local_line],
                                                 &is_local,
                                                 &local_name,
                                                 &initializer) ||
            !is_local ||
            !refii::lua::IsValidLuaIdentifier(local_name) ||
            !IsBytecodeRepairSimpleCallExpression(initializer)) {
            continue;
        }

        const std::size_t return_line = next_nonempty_line(local_line + 1);
        if (return_line >= lines.size()) {
            continue;
        }
        const std::string_view return_trimmed =
            TrimWhitespace(StripLineComment(lines[return_line]));
        if (return_trimmed != "return " + local_name) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[local_line]);
        std::vector<std::string> transformed_lines = lines;
        transformed_lines[return_line] = indent + "if not " + local_name + " then";
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(return_line + 1),
            indent + "  return false");
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(return_line + 2),
            indent + "end");
        transformed_lines.insert(
            transformed_lines.begin() + static_cast<std::ptrdiff_t>(return_line + 3),
            indent + "return true");

        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_local_call_return_boolean_guard",
                candidate_source)) {
            return true;
        }

        std::string transformed_source =
            JoinLines(transformed_lines, had_trailing_newline);
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(
                transformed_source,
                "bytecode_parity_candidate_local_call_return_boolean_guard",
                &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            continue;
        }
        const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
            refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                         transformed_chunk.chunk);
        if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch,
                                            context.mismatch) ||
            !IsBytecodeParityStrictMismatchImprovement(context,
                                                       transformed_mismatch)) {
            continue;
        }

        BytecodeParityRepairContext chained_context{context.original_chunk,
                                                    transformed_chunk,
                                                    transformed_mismatch};
        std::string chained_source = transformed_source;
        if (TryRepairNearExactMismatchedStringLiteralFromParityNote(
                chained_context,
                &chained_source)) {
            *candidate_source = std::move(chained_source);
            return true;
        }

        *candidate_source = std::move(transformed_source);
        return true;
    }

    return false;
}

bool TryRepairNearExactCallTrailingNumericTripletConstants(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("LOADK") == std::string::npos &&
        context.mismatch.note.find("function_stats_differ") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const auto split_args = [](std::string_view body) {
        std::vector<std::string> args;
        std::size_t arg_start = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        int paren_depth = 0;
        int brace_depth = 0;
        int bracket_depth = 0;
        for (std::size_t index = 0; index <= body.size(); ++index) {
            const char ch = index < body.size() ? body[index] : ',';
            if (index < body.size()) {
                if (ch == '\'' && !in_double_quote) {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (ch == '"' && !in_single_quote) {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')' && paren_depth > 0) {
                    --paren_depth;
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}' && brace_depth > 0) {
                    --brace_depth;
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']' && bracket_depth > 0) {
                    --bracket_depth;
                    continue;
                }
            }
            if (ch != ',' || in_single_quote || in_double_quote ||
                paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                continue;
            }
            args.emplace_back(TrimWhitespace(body.substr(arg_start, index - arg_start)));
            arg_start = index + 1;
        }
        return args;
    };

    const auto parse_binary_numeric = [](std::string_view value, int* out) {
        if (out == nullptr) {
            return false;
        }
        const std::string stripped = StripBytecodeRepairWholeParens(value);
        if (stripped == "0") {
            *out = 0;
            return true;
        }
        if (stripped == "255") {
            *out = 255;
            return true;
        }
        return false;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        const std::size_t open = trimmed.find('(');
        if (open == std::string_view::npos || !trimmed.ends_with(")")) {
            continue;
        }
        const std::string_view callee = TrimWhitespace(trimmed.substr(0, open));
        if (callee.empty() || callee.find('=') != std::string_view::npos ||
            callee.find("if ") != std::string_view::npos ||
            callee.find("while ") != std::string_view::npos) {
            continue;
        }

        std::vector<std::string> args =
            split_args(trimmed.substr(open + 1, trimmed.size() - open - 2));
        if (args.size() < 3) {
            continue;
        }

        int current[3] = {0, 0, 0};
        const std::size_t first_triplet = args.size() - 3;
        if (!parse_binary_numeric(args[first_triplet], &current[0]) ||
            !parse_binary_numeric(args[first_triplet + 1], &current[1]) ||
            !parse_binary_numeric(args[first_triplet + 2], &current[2])) {
            continue;
        }

        for (int mask = 0; mask < 8; ++mask) {
            const int candidate_values[3] = {
                (mask & 4) != 0 ? 255 : 0,
                (mask & 2) != 0 ? 255 : 0,
                (mask & 1) != 0 ? 255 : 0,
            };
            if (candidate_values[0] == current[0] &&
                candidate_values[1] == current[1] &&
                candidate_values[2] == current[2]) {
                continue;
            }

            std::vector<std::string> transformed_args = args;
            transformed_args[first_triplet] = std::to_string(candidate_values[0]);
            transformed_args[first_triplet + 1] = std::to_string(candidate_values[1]);
            transformed_args[first_triplet + 2] = std::to_string(candidate_values[2]);
            std::string joined_args;
            for (const std::string& arg : transformed_args) {
                if (!joined_args.empty()) {
                    joined_args += ", ";
                }
                joined_args += arg;
            }

            std::vector<std::string> transformed_lines = lines;
            transformed_lines[line_index] =
                GetLuaLineIndent(lines[line_index]) + std::string(callee) + "(" +
                joined_args + ")";
            if (TryCommitBytecodeParityImprovingLines(
                    context,
                    transformed_lines,
                    had_trailing_newline,
                    "bytecode_parity_candidate_call_trailing_numeric_triplet_constants",
                    candidate_source)) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairNearExactMismatchedStringLiteralFromParityNote(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("LOADK") == std::string::npos &&
        context.mismatch.note.find("first_constant_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 4 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 2 ||
        candidate_source->size() > 256 * 1024) {
        return false;
    }

    std::vector<std::pair<std::string, std::string>> literal_pairs;
    std::string expected_literal;
    std::string actual_literal;
    if (TryExtractBytecodeParityQuotedLiteralPair(context.mismatch.note,
                                                  "first_instruction_diff=",
                                                  &expected_literal,
                                                  &actual_literal)) {
        literal_pairs.emplace_back(std::move(expected_literal),
                                   std::move(actual_literal));
    }
    if (TryExtractBytecodeParityQuotedLiteralPair(context.mismatch.note,
                                                  "first_constant_diff=",
                                                  &expected_literal,
                                                  &actual_literal)) {
        literal_pairs.emplace_back(std::move(expected_literal),
                                   std::move(actual_literal));
    }
    if (literal_pairs.empty()) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines =
        SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    for (const auto& [expected, actual] : literal_pairs) {
        if (expected.empty() || actual.empty() || expected == actual ||
            expected.front() != '"' || actual.front() != '"' ||
            expected.back() != '"' || actual.back() != '"' ||
            expected.size() > 256 || actual.size() > 256) {
            continue;
        }

        bool have_best_improvement = false;
        std::vector<std::string> best_lines;
        refii::lua::audit::FunctionMismatchInfo best_mismatch;
        std::size_t attempts = 0;
        for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
            std::size_t search_from = 0;
            while (search_from < lines[line_index].size()) {
                const std::size_t found = lines[line_index].find(actual, search_from);
                if (found == std::string::npos) {
                    break;
                }
                search_from = found + actual.size();
                if (++attempts > 64) {
                    break;
                }

                std::vector<std::string> transformed_lines = lines;
                transformed_lines[line_index].replace(found, actual.size(), expected);
                if (TryCommitBytecodeParityExactCurrentFunctionLines(
                        context,
                        transformed_lines,
                        had_trailing_newline,
                        "bytecode_parity_candidate_mismatched_string_literal",
                        candidate_source)) {
                    return true;
                }

                std::string transformed_source =
                    JoinLines(transformed_lines, had_trailing_newline);
                refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
                if (!CompileLuaSourceToChunkCached(
                        transformed_source,
                        "bytecode_parity_candidate_mismatched_string_literal_probe",
                        &transformed_chunk) ||
                    !transformed_chunk.chunk_parsed) {
                    continue;
                }
                const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
                    refii::lua::audit::FindFirstFunctionMismatch(
                        context.original_chunk,
                        transformed_chunk.chunk);
                if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch,
                                                    context.mismatch) ||
                    !IsBytecodeParityStrictMismatchImprovement(context,
                                                               transformed_mismatch)) {
                    continue;
                }

                BytecodeParityRepairContext chained_context{
                    context.original_chunk,
                    transformed_chunk,
                    transformed_mismatch};
                std::string chained_expected;
                std::string chained_actual;
                if (TryExtractBytecodeParityQuotedLiteralPair(
                        transformed_mismatch.note,
                        "first_instruction_diff=",
                        &chained_expected,
                        &chained_actual) ||
                    TryExtractBytecodeParityQuotedLiteralPair(
                        transformed_mismatch.note,
                        "first_constant_diff=",
                        &chained_expected,
                        &chained_actual)) {
                    std::size_t chained_attempts = 0;
                    for (std::size_t chained_line = 0;
                         chained_line < transformed_lines.size();
                         ++chained_line) {
                        std::size_t chained_search_from = 0;
                        while (chained_search_from <
                               transformed_lines[chained_line].size()) {
                            const std::size_t chained_found =
                                transformed_lines[chained_line].find(
                                    chained_actual,
                                    chained_search_from);
                            if (chained_found == std::string::npos) {
                                break;
                            }
                            chained_search_from =
                                chained_found + chained_actual.size();
                            if (++chained_attempts > 64) {
                                break;
                            }

                            std::vector<std::string> chained_lines =
                                transformed_lines;
                            chained_lines[chained_line].replace(
                                chained_found,
                                chained_actual.size(),
                                chained_expected);
                            if (TryCommitBytecodeParityExactCurrentFunctionLines(
                                    context,
                                    chained_lines,
                                    had_trailing_newline,
                                    "bytecode_parity_candidate_mismatched_string_literal_chained",
                                    candidate_source)) {
                                return true;
                            }
                        }
                    }
                }

                if (!have_best_improvement ||
                    IsBytecodeParityStrictMismatchImprovement(
                        BytecodeParityRepairContext{context.original_chunk,
                                                    context.compiled_candidate,
                                                    best_mismatch},
                        transformed_mismatch)) {
                    have_best_improvement = true;
                    best_mismatch = transformed_mismatch;
                    best_lines = std::move(transformed_lines);
                }
            }
        }
        if (have_best_improvement &&
            TryCommitBytecodeParityImprovingLines(
                context,
                best_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_mismatched_string_literal_best",
                candidate_source)) {
            return true;
        }
    }

    return false;
}

bool TryRepairNearExactLocalDeclarationToAssignment(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }
    if (std::llabs(context.mismatch.instruction_delta) > 8 ||
        std::llabs(context.mismatch.constant_delta) > 4 ||
        std::llabs(context.mismatch.local_delta) > 4) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(*candidate_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    bool have_best_improvement = false;
    std::string best_source;
    refii::lua::audit::FunctionMismatchInfo best_mismatch;

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        constexpr std::string_view kLocalPrefix = "local ";
        if (!StartsWith(trimmed, kLocalPrefix)) {
            continue;
        }
        const std::size_t eq = trimmed.find('=');
        if (eq == std::string_view::npos || eq <= kLocalPrefix.size() ||
            eq + 1 >= trimmed.size() ||
            trimmed.find(',') != std::string_view::npos) {
            continue;
        }

        const std::string local_name =
            std::string(TrimWhitespace(trimmed.substr(kLocalPrefix.size(),
                                                      eq - kLocalPrefix.size())));
        const std::string rhs = std::string(TrimWhitespace(trimmed.substr(eq + 1)));
        if (!refii::lua::IsValidLuaIdentifier(local_name) || rhs.empty() ||
            refii::lua::IsRenderTemporaryName(local_name)) {
            continue;
        }

        std::vector<std::string> transformed_lines = lines;
        transformed_lines[line_index] =
            GetLuaLineIndent(lines[line_index]) + local_name + " = " + rhs;
        if (TryCommitBytecodeParityExactCurrentFunctionLines(
                context,
                transformed_lines,
                had_trailing_newline,
                "bytecode_parity_candidate_local_declaration_to_assignment",
                candidate_source)) {
            return true;
        }

        std::string transformed_source = JoinLines(transformed_lines, had_trailing_newline);
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(
                transformed_source,
                "bytecode_parity_candidate_local_declaration_to_assignment_best",
                &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            continue;
        }

        const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
            refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                         transformed_chunk.chunk);
        if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch,
                                            context.mismatch) ||
            !IsBytecodeParityStrictMismatchImprovement(context,
                                                       transformed_mismatch)) {
            continue;
        }
        if (!have_best_improvement ||
            IsBytecodeParityStrictMismatchImprovement(
                BytecodeParityRepairContext{context.original_chunk,
                                            context.compiled_candidate,
                                            best_mismatch},
                transformed_mismatch)) {
            have_best_improvement = true;
            best_mismatch = transformed_mismatch;
            best_source = std::move(transformed_source);
        }
    }

    if (have_best_improvement) {
        *candidate_source = std::move(best_source);
        return true;
    }

    return false;
}

bool TryRepairNearExactProgressiveMultiStage(
    const std::vector<char>& input_bytes,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix) {
    if (candidate_source == nullptr) {
        return false;
    }

    std::string working_source = *candidate_source;
    bool changed_any = false;
    std::unordered_set<std::string> seen_states;
    seen_states.insert(ComputeStableBytecodeIdentity(working_source));
    std::size_t entry_mismatch_function_index = std::numeric_limits<std::size_t>::max();

    const bool small_progressive_candidate =
        input_bytes.size() <= 64 * 1024 && candidate_source->size() <= 128 * 1024;
    const int kRoundCap = small_progressive_candidate ? 18 : 4;
    for (int round = 0; round < kRoundCap; ++round) {
        if (ShouldAbortCurrentDecompileRepair(note, note_prefix)) {
            break;
        }
        BytecodeParityRepairContext round_context;
        if (!BuildBytecodeParityRepairContext(input_bytes,
                                              working_source,
                                              &round_context) ||
            !round_context.mismatch.available) {
            break;
        }
        if (entry_mismatch_function_index == std::numeric_limits<std::size_t>::max()) {
            entry_mismatch_function_index = round_context.mismatch.function_index;
        }

        const bool near_exact_shape =
            std::llabs(round_context.mismatch.instruction_delta) <= 2 &&
            std::llabs(round_context.mismatch.constant_delta) <=
                (small_progressive_candidate ? 2 : 1) &&
            std::llabs(round_context.mismatch.local_delta) <= 2 &&
            (round_context.mismatch.note.find("function_stats_differ") != std::string::npos ||
             round_context.mismatch.note.find("instruction_diff") != std::string::npos ||
             round_context.mismatch.note.find("local_diff") != std::string::npos);
        const bool advanced_beyond_entry =
            entry_mismatch_function_index != std::numeric_limits<std::size_t>::max() &&
            round_context.mismatch.function_index > entry_mismatch_function_index;
        const bool advanced_shape =
            advanced_beyond_entry &&
            std::llabs(round_context.mismatch.instruction_delta) <=
                (small_progressive_candidate ? 32 : 8) &&
            std::llabs(round_context.mismatch.constant_delta) <=
                (small_progressive_candidate ? 4 : 1) &&
            std::llabs(round_context.mismatch.local_delta) <=
                (small_progressive_candidate ? 8 : 3) &&
            (round_context.mismatch.note.find("function_stats_differ") != std::string::npos ||
             round_context.mismatch.note.find("instruction_diff") != std::string::npos ||
             round_context.mismatch.note.find("local_diff") != std::string::npos);
        bool round_changed = false;
        if (!near_exact_shape && !advanced_shape) {
            round_changed |= TryRepairNearExactManagerStateLiteralAndCachedUiLocals(
                round_context,
                &working_source);
            if (!round_changed) {
                break;
            }
        }

        if (round_context.mismatch.note.find("local_diff") != std::string::npos) {
            round_changed |= TryRepairNearExactSplitLocalAddInitializer(round_context,
                                                                        &working_source);
            if (!round_changed) {
                round_changed |=
                    TryRepairNearExactFoldLocalDiffAnchoredInitializerUseAssignments(
                        round_context,
                        &working_source);
            }
            if (!round_changed) {
                round_changed |= TryRepairNearExactFoldPairedLocalInitializerUseAssignments(
                    round_context,
                    &working_source);
            }
            if (!round_changed) {
                round_changed |= TryRepairNearExactFoldBatchLocalInitializerUseAssignments(
                    round_context,
                    &working_source);
            }
            if (!round_changed) {
                round_changed |= TryRepairNearExactFoldLocalInitializerUseAssignment(
                    round_context,
                    &working_source);
            }
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactMismatchedStringLiteralFromParityNote(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactConstructorDefaultObjectAndSelfIndex(round_context,
                                                                       &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactComputedRatioClampReuse(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactRepeatedSumDenominatorGrouping(round_context,
                                                                 &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactConstructorEmptyFieldFromParameter(
                    round_context,
                    &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactFunctionTimingCleanup(round_context,
                                                                     &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactMissingElseBranchJump(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactNavigationParamsMaxSpeedClamp(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactGuardedMessageElseQuestPrimaryBundle(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactEmptyGuardReturnShortCircuit(round_context,
                                                                            &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactGuardedReturnFalseShortCircuit(
                round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactNestedGuardBooleanReturnExpression(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactIfReturnBooleanToReturnExpression(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactHoistPairsCallIteratorAndDropBreak(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactTableInsertInlineLiteralTemp(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactTopLevelDottedFieldTableSeedCluster(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactReturnedLocalShadowAssignment(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactSplitLocalAddInitializer(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactLocalCopyInitializerExpression(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactFoldLocalSeedAddAssignment(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactCopyInitializerAndFoldSeedAdd(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairMultiResultTempFieldAssignments(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairRepeatedScalarCallFromMultiResultSeed(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairMalformedDofAssemblyConditionExpressions(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactBranchScopedRedeclaredLocal(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactFoldSeedLocalIntoMultiAssignmentCall(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactFoldLocalDiffAnchoredInitializerUseAssignments(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactFoldBatchLocalInitializerUseAssignments(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactFoldPairedLocalInitializerUseAssignments(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactFoldLocalInitializerUseAssignment(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactDuplicateBreakGuardComparison(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactDuplicateGroupMethodCallChain(
                round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactAssertIfElseBoolean(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactAssertNilPolarity(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactReferenceCarrySingletonPatchFallback(
                round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactSingleUseLocalTableCallLiteral(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactActionTableSeedAndCallLocalReuse(
                round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactLocalSelfTableLiteralFieldWrites(
                round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactEmptyLocalSelfSeed(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactActionCallTableLiteralHoist(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactSingletonTableOverwriteToFieldPatch(round_context,
                                                                      &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactSingletonTableAssignmentToFieldPatch(round_context,
                                                                       &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactAliasConstructorToFieldPatch(round_context,
                                                               &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactRedundantSelfAssignment(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactRedundantTablePathSeedAssignment(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactDuplicateCallReassignment(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactDuplicateGlobalTableSelfSeed(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactDeadInlinedTempTableLiteralArgs(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactReturnedBooleanReassignedTempTableLiteral(
                    round_context,
                    &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactReturnedBooleanTempTableLiteralBulk(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactReturnedSelfSeedFieldTableLiteral(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactReturnedGroupMindTempTableLiteral(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactReturnedGroupMindConditionalTempSeed(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactGroupMindAttachedGuardCallLocal(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactTablePathIdentifierSeedToEmptyTable(round_context,
                                                                      &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactLocalTableLiteralSizeHintExpansion(round_context,
                                                                     &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactLocalIdentifierSeedToEmptyTable(round_context,
                                                                  &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactObjectScalarIntoSequenceTableField(round_context,
                                                                     &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactConstructorParamEmptyAliasSwap(round_context,
                                                                &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactConstructorEmptyFieldFromParameter(
                    round_context,
                    &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactLocalTableLiteralToFieldWrites(round_context,
                                                                 &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactRedundantIndexedTableAssignmentBeforeFieldWrite(
                    round_context,
                    &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactLoopAssignmentBreak(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactLoopTempTableInsertShape(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactLoopCallBreak(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactRedundantPostFailureBreakGuard(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactTableLiteralAssignmentToFieldWrites(round_context,
                                                                      &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactEqualityOperandSwap(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactRelationalOperandSwap(round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactRelationalLocalCallOperandInlineSwap(
                    round_context,
                    &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactPairedNegativeGuardCallCarry(round_context,
                                                               &working_source);
        }
            if (!round_changed) {
                round_changed |=
                    TryRepairNearExactNestedCallArgumentLocal(round_context, &working_source);
            }
            if (!round_changed) {
                round_changed |= TryRepairNearExactCallArgumentLocalOrderSwap(
                    round_context,
                    &working_source);
            }
            if (!round_changed) {
                round_changed |= TryRepairNearExactManagerStateLiteralAndCachedUiLocals(
                    round_context,
                    &working_source);
            }
            if (!round_changed) {
                round_changed |=
                    TryRepairNearExactRepeatedCallArgumentLocal(round_context, &working_source);
            }
            if (!round_changed) {
                round_changed |=
                    TryRepairNearExactReturnCallBooleanGuard(round_context, &working_source);
            }
        if (!round_changed) {
            round_changed |= TryRepairNearExactWrapReturnedCallForNonTailCall(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactInlineLocalCallBooleanReturnGuard(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactRemoveBreakGuardBeforeYield(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactTopLevelTableSeedFieldsForCurrentFunction(
                    round_context,
                    &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactLocalCallReturnBooleanGuard(round_context,
                                                              &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactCallTrailingNumericTripletConstants(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactMismatchedStringLiteralFromParityNote(
                round_context,
                &working_source);
        }
        if (!round_changed) {
            round_changed |= TryRepairNearExactLocalDeclarationToAssignment(
                round_context,
                &working_source);
        }

        if (!round_changed) {
            break;
        }

        BytecodeParityRepairContext chained_context;
        if (BuildBytecodeParityRepairContext(input_bytes,
                                             working_source,
                                             &chained_context) &&
            chained_context.mismatch.available) {
            bool chained_changed = false;
            chained_changed |=
                TryRepairNearExactComputedRatioClampReuse(chained_context,
                                                          &working_source);
            if (!chained_changed) {
                chained_changed |=
                    TryRepairNearExactRepeatedSumDenominatorGrouping(chained_context,
                                                                     &working_source);
            }
            if (chained_changed && note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_bytecode_near_exact_multistage_chained_ratio");
            }
        }

        changed_any = true;
        if (!seen_states.insert(ComputeStableBytecodeIdentity(working_source)).second) {
            break;
        }
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_multistage_round=" +
                                  std::to_string(round + 1));
        }
    }

    if (!changed_any || working_source == *candidate_source) {
        return false;
    }

    *candidate_source = std::move(working_source);
    return true;
}

bool ShouldSkipBytecodeParityRepairRuntimeWatchdog(
    const std::vector<char>& input_bytes,
    std::string_view candidate_source,
    std::string* note,
    std::string_view note_prefix) {
    const DecompileRepairContext& context = GetCurrentDecompileRepairContext();
    if (!context.deadline_active) {
        return false;
    }

    const auto now = std::chrono::steady_clock::now();
    if (now >= context.deadline) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_runtime_watchdog_deadline_reached");
        }
        return true;
    }

    const auto remaining_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                                  context.deadline - now)
                                  .count();
    if (remaining_ms > 20000) {
        return false;
    }

    if (input_bytes.size() < 96 * 1024 && candidate_source.size() < 160 * 1024) {
        return false;
    }

    if (note != nullptr && !note_prefix.empty()) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) + "_runtime_watchdog_skip_remaining_ms=" +
                std::to_string(std::max<long long>(remaining_ms, 0)));
    }
    return true;
}

}  // namespace

bool TryRepairNativeCandidateBytecodeParityWithCallbacks(
    const std::vector<char>& input_bytes,
    std::string& candidate_source,
    std::string* note,
    std::string_view note_prefix,
    const refii::lua::LuaWorkspaceDecompileOptions* options,
    const std::filesystem::path* input_path,
    const ExactBytecodeParityRepairPipelineCallbacks& callbacks) {
    const ExactBytecodeParityRepairPipelineCallbacks* previous_callbacks =
        g_active_callbacks;
    g_active_callbacks = &callbacks;
    struct CallbackScope {
        const ExactBytecodeParityRepairPipelineCallbacks* previous = nullptr;
        ~CallbackScope() {
            g_active_callbacks = previous;
        }
    } callback_scope{previous_callbacks};

    if (ShouldAbortCurrentDecompileRepair(note, note_prefix)) {
        return false;
    }
    if (ShouldSkipBytecodeParityRepairRuntimeWatchdog(
            input_bytes, candidate_source, note, note_prefix)) {
        return false;
    }
    BytecodeParityRepairContext context;
    if (!BuildBytecodeParityRepairContext(input_bytes, candidate_source, &context)) {
        return false;
    }

    if (TryRepairNearExactConstructorDefaultObjectAndSelfIndex(context,
                                                               &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_constructor_default_object_self_index");
        }
        return true;
    }

    if (TryRepairNearExactComputedRatioClampReuse(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_computed_ratio_clamp_reuse");
        }
        return true;
    }

    if (TryRepairNearExactRepeatedSumDenominatorGrouping(context,
                                                         &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_repeated_sum_denominator_grouping");
        }
        return true;
    }

    if (TryRepairNearExactDuplicateGlobalTableSelfSeed(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_duplicate_global_table_self_seed");
        }
        return true;
    }

    if (TryRepairNearExactDeadInlinedTempTableLiteralArgs(context,
                                                         &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_dead_inlined_temp_table_literal_args");
        }
        return true;
    }

    if (TryRepairNearExactReturnedBooleanReassignedTempTableLiteral(
            context,
            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_boolean_reassigned_temp_table_literal");
        }
        return true;
    }

    if (TryRepairNearExactReturnedBooleanTempTableLiteralBulk(context,
                                                             &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_boolean_temp_table_literal_bulk");
        }
        return true;
    }

    if (TryRepairNearExactConstructorEmptyFieldFromParameter(context,
                                                            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_constructor_empty_field_parameter");
        }
        return true;
    }

    if (TryRepairNearExactProgressiveMultiStage(input_bytes,
                                                &candidate_source,
                                                note,
                                                note_prefix)) {
        return true;
    }

    if (TryRepairMissingElseTrailingFunctionSuffix(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_bytecode_else_suffix_repaired");
        }
        return true;
    }

    if (TryRepairWhileConditionInitializerCallCarry(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_bytecode_while_condition_repaired");
        }
        return true;
    }

    if (TryRepairLocalDiffParameterNameMismatch(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_localdiff_parameter_renamed");
        }
        return true;
    }

    if (TryRepairTinyMainCreateSubClassSeedOwnership(
            context, &candidate_source, MakePipelineTinyMainRepairOptions())) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_tiny_main_subclass_seed_exact");
        }
        return true;
    }

    if (TryRepairTinyMainSingletonIndexTableAssignments(
            context, &candidate_source, MakePipelineTinyMainRepairOptions())) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_tiny_main_singleton_index_exact");
        }
        return true;
    }

    if (TryRepairTinyMainRepeatedGlobalFieldWriteAlias(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_tiny_main_global_alias_exact");
        }
        return true;
    }

    if (TryRepairTinyMainTopLevelFieldSeedCluster(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_tiny_main_field_seed_cluster_exact");
        }
        return true;
    }

    if (TryRepairTinyGroupMembersSingletonFieldAssignments(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_groupmembers_singleton_field_exact");
        }
        return true;
    }

    if (context.mismatch.note.find("local_diff") != std::string::npos &&
        TryRepairNearExactSplitLocalAddInitializer(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_split_local_add_initializer");
        }
        return true;
    }

    if (context.mismatch.note.find("local_diff") != std::string::npos &&
        TryRepairNearExactFoldLocalDiffAnchoredInitializerUseAssignments(
            context,
            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_local_diff_anchored_initializer_use_assignments");
        }
        return true;
    }

    if (context.mismatch.note.find("local_diff") != std::string::npos &&
        TryRepairNearExactFoldPairedLocalInitializerUseAssignments(context,
                                                                  &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_paired_local_initializer_use_assignments");
        }
        return true;
    }

    if (context.mismatch.note.find("local_diff") != std::string::npos &&
        TryRepairNearExactFoldBatchLocalInitializerUseAssignments(context,
                                                                 &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_batch_local_initializer_use_assignments");
        }
        return true;
    }

    if (context.mismatch.note.find("local_diff") != std::string::npos &&
        TryRepairNearExactFoldLocalInitializerUseAssignment(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_local_initializer_use_assignment");
        }
        return true;
    }

    if (TryRepairMultiResultTempFieldAssignments(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_multi_result_temp_field_assignments");
        }
        return true;
    }

    if (TryRepairRepeatedScalarCallFromMultiResultSeed(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_repeated_scalar_call_from_multi_result_seed");
        }
        return true;
    }

    if (TryRepairMalformedDofAssemblyConditionExpressions(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_malformed_dof_assembly_condition_expressions");
        }
        return true;
    }

    if (TryRepairNearExactBranchScopedRedeclaredLocal(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_branch_scoped_redeclared_local");
        }
        return true;
    }

    if (TryRepairNearExactFoldSeedLocalIntoMultiAssignmentCall(context,
                                                              &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_fold_seed_local_into_multi_assignment_call");
        }
        return true;
    }

    if (TryRepairNearExactFunctionTimingCleanup(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_timing_exact");
        }
        return true;
    }

    if (TryRepairNearExactMissingElseBranchJump(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_missing_else_jump_exact");
        }
        return true;
    }

    if (TryRepairNearExactNavigationParamsMaxSpeedClamp(context,
                                                       &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_navigation_params_max_speed_clamp");
        }
        return true;
    }

    if (TryRepairNearExactGuardedMessageElseQuestPrimaryBundle(context,
                                                              &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_guarded_message_else_quest_primary_bundle");
        }
        return true;
    }

    if (TryRepairNearExactNegatedAndGuardToPositiveOrElse(context,
                                                          &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_negated_and_guard_to_positive_or_else");
        }
        return true;
    }

    if (TryRepairNearExactEmptyGuardReturnShortCircuit(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_empty_guard_return_short_circuit");
        }
        return true;
    }

    if (TryRepairNearExactGuardedReturnFalseShortCircuit(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_guarded_return_false_short_circuit");
        }
        return true;
    }

    if (TryRepairNearExactNestedGuardBooleanReturnExpression(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_nested_guard_boolean_return");
        }
        return true;
    }

    if (TryRepairNearExactIfReturnBooleanToReturnExpression(context,
                                                            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_if_return_boolean_to_return_expression");
        }
        return true;
    }

    if (TryRepairNearExactHoistPairsCallIteratorAndDropBreak(context,
                                                             &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_hoist_pairs_call_iterator_drop_break");
        }
        return true;
    }

    if (TryRepairNearExactTableInsertInlineLiteralTemp(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_table_insert_inline_literal_temp");
        }
        return true;
    }

    if (TryRepairNearExactTopLevelDottedFieldTableSeedCluster(context,
                                                              &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_top_level_dotted_field_seed_cluster");
        }
        return true;
    }

    if (TryRepairNearExactReturnedLocalShadowAssignment(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_local_shadow_assignment");
        }
        return true;
    }

    if (TryRepairNearExactSplitLocalAddInitializer(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_split_local_add_initializer");
        }
        return true;
    }

    if (TryRepairNearExactLocalCopyInitializerExpression(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_local_copy_initializer_expression");
        }
        return true;
    }

    if (TryRepairNearExactFoldLocalSeedAddAssignment(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_local_seed_add_assignment");
        }
        return true;
    }

    if (TryRepairNearExactCopyInitializerAndFoldSeedAdd(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_copy_initializer_and_fold_seed_add");
        }
        return true;
    }

    if (TryRepairNearExactFoldLocalDiffAnchoredInitializerUseAssignments(
            context,
            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_local_diff_anchored_initializer_use_assignments");
        }
        return true;
    }

    if (TryRepairNearExactFoldBatchLocalInitializerUseAssignments(context,
                                                                 &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_batch_local_initializer_use_assignments");
        }
        return true;
    }

    if (TryRepairNearExactFoldPairedLocalInitializerUseAssignments(context,
                                                                  &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_paired_local_initializer_use_assignments");
        }
        return true;
    }

    if (TryRepairNearExactFoldLocalInitializerUseAssignment(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_fold_local_initializer_use_assignment");
        }
        return true;
    }

    if (TryRepairNearExactDuplicateBreakGuardComparison(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_duplicate_break_guard_exact");
        }
        return true;
    }

    if (TryRepairNearExactSingletonTableOverwriteToFieldPatch(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_singleton_overwrite_patch");
        }
        return true;
    }

    if (TryRepairNearExactSingletonTableAssignmentToFieldPatch(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_singleton_table_assignment_field_patch");
        }
        return true;
    }

    if (TryRepairNearExactAliasConstructorToFieldPatch(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_alias_constructor_field_patch");
        }
        return true;
    }

    if (TryRepairNearExactRedundantSelfAssignment(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_redundant_self_assignment");
        }
        return true;
    }

    if (TryRepairNearExactRedundantTablePathSeedAssignment(context,
                                                          &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_redundant_table_path_seed_assignment");
        }
        return true;
    }

    if (TryRepairNearExactDuplicateCallReassignment(context,
                                                   &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_duplicate_call_reassignment");
        }
        return true;
    }

    if (TryRepairNearExactDuplicateGlobalTableSelfSeed(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_duplicate_global_table_self_seed");
        }
        return true;
    }

    if (TryRepairNearExactDeadInlinedTempTableLiteralArgs(context,
                                                         &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_dead_inlined_temp_table_literal_args");
        }
        return true;
    }

    if (TryRepairNearExactReturnedBooleanReassignedTempTableLiteral(
            context,
            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_boolean_reassigned_temp_table_literal");
        }
        return true;
    }

    if (TryRepairNearExactReturnedBooleanTempTableLiteralBulk(context,
                                                             &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_boolean_temp_table_literal_bulk");
        }
        return true;
    }

    if (TryRepairNearExactReturnedSelfSeedFieldTableLiteral(context,
                                                           &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_self_seed_field_table_literal");
        }
        return true;
    }

    if (TryRepairNearExactReturnedGroupMindTempTableLiteral(context,
                                                           &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_groupmind_temp_table_literal");
        }
        return true;
    }

    if (TryRepairNearExactReturnedGroupMindConditionalTempSeed(context,
                                                              &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_groupmind_conditional_temp_seed");
        }
        return true;
    }

    if (TryRepairNearExactGroupMindAttachedGuardCallLocal(context,
                                                         &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_groupmind_attached_guard_call_local");
        }
        return true;
    }

    if (TryRepairNearExactTablePathIdentifierSeedToEmptyTable(context,
                                                             &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_table_path_identifier_seed_empty_table");
        }
        return true;
    }

    if (TryRepairNearExactLocalTableLiteralSizeHintExpansion(context,
                                                            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_local_table_literal_size_hint_expansion");
        }
        return true;
    }

    if (TryRepairNearExactLocalIdentifierSeedToEmptyTable(context,
                                                         &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_local_identifier_seed_empty_table");
        }
        return true;
    }

    if (TryRepairNearExactTableLiteralAssignmentToFieldWrites(context,
                                                             &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_table_literal_assignment_field_writes");
        }
        return true;
    }

    if (TryRepairNearExactObjectScalarIntoSequenceTableField(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_object_scalar_sequence_table_field");
        }
        return true;
    }

    if (TryRepairNearExactConstructorParamEmptyAliasSwap(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_constructor_param_empty_alias_swap");
        }
        return true;
    }

    if (TryRepairNearExactConstructorEmptyFieldFromParameter(context,
                                                            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_constructor_empty_field_parameter");
        }
        return true;
    }

    if (TryRepairNearExactLocalTableLiteralToFieldWrites(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_local_table_literal_field_writes");
        }
        return true;
    }

    if (TryRepairNearExactRedundantIndexedTableAssignmentBeforeFieldWrite(
            context,
            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_remove_indexed_table_assignment");
        }
        return true;
    }

    if (TryRepairNearExactLoopAssignmentBreak(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_loop_assignment_break");
        }
        return true;
    }

    if (TryRepairNearExactLoopTempTableInsertShape(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_loop_temp_table_insert_shape");
        }
        return true;
    }

    if (TryRepairNearExactLoopCallBreak(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_loop_call_break");
        }
        return true;
    }

    if (TryRepairNearExactRedundantPostFailureBreakGuard(context,
                                                        &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_redundant_post_failure_break_guard");
        }
        return true;
    }

    if (TryRepairNearExactReturnedTableTempLiteral(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_returned_table_temp_literal");
        }
        return true;
    }

    if (TryRepairNearExactEqualityOperandSwap(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_equality_operand_swap");
        }
        return true;
    }

    if (TryRepairNearExactRelationalOperandSwap(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_relational_operand_swap");
        }
        return true;
    }

    if (TryRepairNearExactRelationalLocalCallOperandInlineSwap(context,
                                                              &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_relational_local_call_inline_swap");
        }
        return true;
    }

    if (TryRepairNearExactPairedNegativeGuardCallCarry(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_paired_negative_guard_call_carry");
        }
        return true;
    }

    if (TryRepairNearExactNestedCallArgumentLocal(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_nested_call_argument_local");
        }
        return true;
    }

    if (TryRepairNearExactCallArgumentLocalOrderSwap(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_call_argument_local_order_swap");
        }
        return true;
    }

    if (TryRepairNearExactManagerStateLiteralAndCachedUiLocals(context,
                                                              &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_manager_state_literal_cached_ui_locals");
        }
        return true;
    }

    if (TryRepairNearExactRepeatedCallArgumentLocal(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_repeated_call_argument_local");
        }
        return true;
    }

    if (TryRepairNearExactReturnCallBooleanGuard(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_return_call_boolean_guard");
        }
        return true;
    }

    if (TryRepairNearExactWrapReturnedCallForNonTailCall(context,
                                                        &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_wrap_returned_call_for_non_tailcall");
        }
        return true;
    }

    if (TryRepairNearExactInlineLocalCallBooleanReturnGuard(context,
                                                           &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_inline_local_call_boolean_return_guard");
        }
        return true;
    }

    if (TryRepairNearExactRemoveBreakGuardBeforeYield(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_remove_break_guard_before_yield");
        }
        return true;
    }

    if (TryRepairNearExactTopLevelTableSeedFieldsForCurrentFunction(
            context,
            &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_top_level_table_seed_fields_current");
        }
        return true;
    }

    if (TryRepairNearExactLocalCallReturnBooleanGuard(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_local_call_return_boolean_guard");
        }
        return true;
    }

    if (TryRepairNearExactCallTrailingNumericTripletConstants(context,
                                                             &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_call_trailing_numeric_triplet_constants");
        }
        return true;
    }

    if (TryRepairNearExactMismatchedStringLiteralFromParityNote(context,
                                                               &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_mismatched_string_literal");
        }
        return true;
    }

    if (TryRepairNearExactLocalDeclarationToAssignment(context,
                                                      &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_local_declaration_to_assignment");
        }
        return true;
    }

    if (TryRepairNearExactSingleUseLocalTableCallLiteral(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_single_use_table_call_exact");
        }
        return true;
    }

    if (TryRepairNearExactLastMessageIteratorSeed(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_last_message_iterator_seed");
        }
        return true;
    }

    if (TryRepairNearExactIndexedTableOverwriteLiteralChain(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_indexed_table_literal_chain");
        }
        return true;
    }

    if (TryRepairNearExactActionTableSeedAndCallLocalReuse(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_action_table_seed_and_call_reuse");
        }
        return true;
    }

    if (TryRepairNearExactLocalSelfTableLiteralFieldWrites(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_local_self_table_field_writes");
        }
        return true;
    }

    if (TryRepairNearExactConstructorDefaultObjectAndSelfIndex(context,
                                                               &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_constructor_default_object_self_index");
        }
        return true;
    }

    if (TryRepairNearExactComputedRatioClampReuse(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_computed_ratio_clamp_reuse");
        }
        return true;
    }

    if (TryRepairNearExactRepeatedSumDenominatorGrouping(context,
                                                         &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_repeated_sum_denominator_grouping");
        }
        return true;
    }

    if (TryRepairNearExactEmptyLocalSelfSeed(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_near_exact_empty_local_self_seed");
        }
        return true;
    }

    if (TryRepairNearExactActionCallTableLiteralHoist(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_action_call_table_hoist");
        }
        return true;
    }

    const DecompileRepairContext& repair_context = GetCurrentDecompileRepairContext();
    const bool skip_reference_backed_parity_repairs =
        !repair_context.prefer_exhaustive_rescue &&
        (input_bytes.size() >= 96 * 1024 || candidate_source.size() >= 160 * 1024 ||
         repair_context.outlier_candidate);
    if (skip_reference_backed_parity_repairs) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    "_bytecode_reference_backed_repairs_skipped_fast_path");
        }
        return false;
    }

    if (TryRepairTinyMainTopLevelFromReference(context,
                                               input_bytes,
                                               input_path,
                                               &candidate_source,
                                               note,
                                               note_prefix,
                                               TryPipelineRenderNativeMainChunkCandidate)) {
        return true;
    }

    if (TryRepairTinyMainFunctionStatsFromReference(context,
                                                    input_bytes,
                                                    input_path,
                                                    &candidate_source,
                                                    note,
                                                    note_prefix,
                                                    TryPipelineRenderNativeMainChunkCandidate,
                                                    ApplyPipelineInstructionCleanupBundle)) {
        return true;
    }

    if (TryRepairInstructionDiffFunctionFromReference(context,
                                                      input_bytes,
                                                      input_path,
                                                      &candidate_source,
                                                      note,
                                                      note_prefix,
                                                      TryPipelineRenderNativeMainChunkCandidate)) {
        return true;
    }

    return false;
}

}  // namespace refii::lua::internal
