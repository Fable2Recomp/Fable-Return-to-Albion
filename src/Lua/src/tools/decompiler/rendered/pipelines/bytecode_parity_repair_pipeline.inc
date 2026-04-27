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

    const bool changed = ApplyRenderedLuaNearExactInstructionCleanupBundle(
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
    if (TryApplyRenderedLuaNearExactInstructionPolarityCleanup(&polarity_lines) &&
        try_commit_transformed_lines(polarity_lines,
                                     "bytecode_parity_candidate_near_exact_polarity")) {
        return true;
    }

    std::vector<std::string> polarity_from_original = SplitLines(*candidate_source,
                                                                 &had_trailing_newline);
    if (TryApplyRenderedLuaNearExactInstructionPolarityCleanup(&polarity_from_original) &&
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

    const auto mismatch_delta_sum =
        [](const refii::lua::audit::FunctionMismatchInfo& mismatch) {
            if (!mismatch.available) {
                return 0LL;
            }
            return std::llabs(mismatch.instruction_delta) +
                   std::llabs(mismatch.constant_delta) +
                   std::llabs(mismatch.local_delta);
        };

    const auto parse_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs;
            if (!refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs) ||
                lhs_names.size() != 1) {
                return false;
            }
            *is_local_out = is_local;
            *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs));
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

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        if (!parse_assignment(lines[line_index], &first_is_local, &first_lhs, &first_rhs) ||
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
        if (!parse_assignment(lines[second_line],
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
        if (try_commit(transformed_lines,
                       "bytecode_parity_candidate_near_exact_singleton_overwrite_patch")) {
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
             mismatch_delta_sum(transformed_mismatch) <
                 mismatch_delta_sum(context.mismatch));
        if (!strict_mismatch_improvement) {
            return false;
        }

        *candidate_source = transformed_source;
        return true;
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
        if (try_commit(transformed_lines,
                       "bytecode_parity_candidate_near_exact_single_use_table_call_inline")) {
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

    auto find_matching_brace = [](std::string_view line, std::size_t open_pos) {
        int depth = 0;
        for (std::size_t i = open_pos; i < line.size(); ++i) {
            const char ch = line[i];
            if (ch == '{') {
                ++depth;
            } else if (ch == '}') {
                --depth;
                if (depth == 0) {
                    return i;
                }
            }
        }
        return std::string_view::npos;
    };

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
        const std::size_t table_close = find_matching_brace(stripped, table_open);
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
        if (try_commit(transformed_lines,
                       "bytecode_parity_candidate_near_exact_action_call_table_hoist")) {
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

    const auto mismatch_delta_sum =
        [](const refii::lua::audit::FunctionMismatchInfo& mismatch) {
            if (!mismatch.available) {
                return 0LL;
            }
            return std::llabs(mismatch.instruction_delta) +
                   std::llabs(mismatch.constant_delta) +
                   std::llabs(mismatch.local_delta);
        };

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

        if (try_commit(transformed_lines,
                       "bytecode_parity_candidate_near_exact_duplicate_break_guard")) {
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
    seen_states.insert(working_source);
    std::size_t entry_mismatch_function_index = std::numeric_limits<std::size_t>::max();

    constexpr int kRoundCap = 4;
    for (int round = 0; round < kRoundCap; ++round) {
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
            std::llabs(round_context.mismatch.constant_delta) <= 1 &&
            std::llabs(round_context.mismatch.local_delta) <= 2 &&
            (round_context.mismatch.note.find("function_stats_differ") != std::string::npos ||
             round_context.mismatch.note.find("instruction_diff") != std::string::npos);
        const bool advanced_beyond_entry =
            entry_mismatch_function_index != std::numeric_limits<std::size_t>::max() &&
            round_context.mismatch.function_index > entry_mismatch_function_index;
        const bool advanced_shape =
            advanced_beyond_entry &&
            std::llabs(round_context.mismatch.instruction_delta) <= 8 &&
            std::llabs(round_context.mismatch.constant_delta) <= 1 &&
            std::llabs(round_context.mismatch.local_delta) <= 3 &&
            (round_context.mismatch.note.find("function_stats_differ") != std::string::npos ||
             round_context.mismatch.note.find("instruction_diff") != std::string::npos);
        if (!near_exact_shape && !advanced_shape) {
            break;
        }

        bool round_changed = false;
        round_changed |= TryRepairNearExactFunctionTimingCleanup(round_context, &working_source);
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactMissingElseBranchJump(round_context, &working_source);
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
            round_changed |= TryRepairNearExactReferenceCarrySingletonPatchFallback(
                round_context, &working_source);
        }
        if (!round_changed) {
            round_changed |=
                TryRepairNearExactSingleUseLocalTableCallLiteral(round_context, &working_source);
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
            break;
        }

        changed_any = true;
        if (!seen_states.insert(working_source).second) {
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

bool TryRepairNativeCandidateBytecodeParity(const std::vector<char>& input_bytes,
                                            std::string& candidate_source,
                                            std::string* note,
                                            std::string_view note_prefix,
                                            const refii::lua::LuaWorkspaceDecompileOptions* options = nullptr,
                                            const std::filesystem::path* input_path = nullptr) {
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

    if (TryRepairTinyMainCreateSubClassSeedOwnership(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_tiny_main_subclass_seed_exact");
        }
        return true;
    }

    if (TryRepairTinyMainSingletonIndexTableAssignments(context, &candidate_source)) {
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

    if (TryRepairNearExactSingleUseLocalTableCallLiteral(context, &candidate_source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_near_exact_single_use_table_call_exact");
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

    if (TryRepairTinyMainTopLevelFromReference(context,
                                               input_bytes,
                                               input_path,
                                               &candidate_source,
                                               note,
                                               note_prefix)) {
        return true;
    }

    if (TryRepairTinyMainFunctionStatsFromReference(context,
                                                    input_bytes,
                                                    input_path,
                                                    &candidate_source,
                                                    note,
                                                    note_prefix)) {
        return true;
    }

    if (TryRepairInstructionDiffFunctionFromReference(context,
                                                      input_bytes,
                                                      input_path,
                                                      &candidate_source,
                                                      note,
                                                      note_prefix)) {
        return true;
    }

    return false;
}
