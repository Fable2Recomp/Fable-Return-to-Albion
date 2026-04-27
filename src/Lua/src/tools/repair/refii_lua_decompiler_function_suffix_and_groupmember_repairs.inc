bool TryRepairMissingElseTrailingFunctionSuffix(const BytecodeParityRepairContext& context,
                                                std::string* candidate_source) {
    if (candidate_source == nullptr || context.mismatch.function_index == 0 ||
        context.mismatch.instruction_delta != -1 || context.mismatch.constant_delta != 0 ||
        context.mismatch.local_delta != 0) {
        return false;
    }

    const refii::lua::Lua51Function* function =
        GetLuaFunctionByPreorderIndex(context.compiled_candidate.chunk, context.mismatch.function_index);
    if (function == nullptr || function->line_defined == 0 || function->last_line_defined == 0) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
    std::vector<std::string>* original_lines_ptr =
        candidate_context.EnsureMutableLines();
    if (original_lines_ptr == nullptr || original_lines_ptr->empty()) {
        return false;
    }
    const bool had_trailing_newline = candidate_context.HadTrailingNewline();
    const std::vector<std::string>& original_lines = *original_lines_ptr;

    const std::size_t function_start = static_cast<std::size_t>(function->line_defined - 1);
    const std::size_t function_end = static_cast<std::size_t>(function->last_line_defined - 1);
    if (function_start >= original_lines.size() || function_end >= original_lines.size() ||
        function_start >= function_end) {
        return false;
    }

    std::size_t candidate_if_line = original_lines.size();
    std::size_t candidate_end_line = original_lines.size();
    std::string candidate_indent;
    std::string candidate_child_indent;

    for (std::size_t line_index = function_start + 1; line_index < function_end; ++line_index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(original_lines[line_index]));
        if (!StartsWith(trimmed, "if ") || !trimmed.ends_with(" then")) {
            continue;
        }

        bool has_else = false;
        std::size_t end_line = 0;
        if (!TryFindLuaRenderedBlockEnd(original_lines,
                                        line_index,
                                        function_end - 1,
                                        &end_line,
                                        &has_else) ||
            has_else || end_line + 1 >= function_end) {
            continue;
        }

        bool has_suffix = false;
        for (std::size_t probe = end_line + 1; probe < function_end; ++probe) {
            if (!TrimWhitespace(StripLineComment(original_lines[probe])).empty()) {
                has_suffix = true;
                break;
            }
        }
        if (!has_suffix) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(original_lines[line_index]);
        std::string child_indent;
        for (std::size_t probe = line_index + 1; probe < end_line; ++probe) {
            const std::string_view probe_trimmed =
                TrimWhitespace(StripLineComment(original_lines[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            child_indent = GetLuaLineIndent(original_lines[probe]);
            break;
        }
        if (child_indent.size() <= indent.size()) {
            continue;
        }

        candidate_if_line = line_index;
        candidate_end_line = end_line;
        candidate_indent = indent;
        candidate_child_indent = child_indent.substr(indent.size());
    }

    if (candidate_if_line >= original_lines.size() || candidate_end_line >= original_lines.size() ||
        candidate_child_indent.empty()) {
        return false;
    }

    std::vector<std::string> transformed_lines = original_lines;
    transformed_lines[candidate_end_line] = candidate_indent + "else";
    for (std::size_t line_index = candidate_end_line + 1; line_index < function_end; ++line_index) {
        if (TrimWhitespace(StripLineComment(transformed_lines[line_index])).empty()) {
            continue;
        }
        transformed_lines[line_index] = candidate_child_indent + transformed_lines[line_index];
    }
    transformed_lines.insert(transformed_lines.begin() +
                                 static_cast<std::ptrdiff_t>(function_end),
                             candidate_indent + "end");

    refii::lua::internal::LuaMutableTextContext transformed_context;
    transformed_context.SetLines(std::move(transformed_lines), had_trailing_newline);
    const std::string transformed_source =
        std::string(transformed_context.EnsureSource());
    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(transformed_source,
                                       "bytecode_parity_candidate_else",
                                       &transformed_chunk) ||
        !transformed_chunk.chunk_parsed) {
        return false;
    }

    if (!IsLuaFunctionStatsImproved(context.original_chunk,
                                    context.compiled_candidate.chunk,
                                    transformed_chunk.chunk,
                                    context.mismatch.function_index)) {
        return false;
    }

    *candidate_source = transformed_source;
    return true;
}

bool TryRepairTinyGroupMembersSingletonFieldAssignments(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr || context.mismatch.function_index == 0 ||
        std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) != 0) {
        return false;
    }

    const refii::lua::Lua51Function* target_function = GetLuaFunctionByPreorderIndex(
        context.compiled_candidate.chunk, context.mismatch.function_index);
    if (target_function == nullptr || target_function->line_defined <= 0 ||
        target_function->last_line_defined <= 0) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
    std::vector<std::string>* lines_ptr = candidate_context.EnsureMutableLines();
    if (lines_ptr == nullptr || lines_ptr->empty()) {
        return false;
    }
    std::vector<std::string>& lines = *lines_ptr;

    const std::size_t target_function_start =
        static_cast<std::size_t>(target_function->line_defined - 1);
    const std::size_t target_function_end =
        static_cast<std::size_t>(target_function->last_line_defined - 1);
    if (target_function_start >= lines.size() || target_function_end >= lines.size() ||
        target_function_start >= target_function_end) {
        return false;
    }

    const auto parse_singleton_table_field =
        [](std::string_view rhs,
           std::string* key_out,
           std::string* value_out) -> bool {
            if (key_out == nullptr || value_out == nullptr) {
                return false;
            }
            *key_out = std::string();
            *value_out = std::string();

            const std::string_view rhs_trimmed = refii::lua::TrimLuaWhitespace(rhs);
            if (rhs_trimmed.size() < 5 || rhs_trimmed.front() != '{' ||
                rhs_trimmed.back() != '}') {
                return false;
            }

            const std::string_view body = refii::lua::TrimLuaWhitespace(
                rhs_trimmed.substr(1, rhs_trimmed.size() - 2));
            if (body.empty()) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t equals_pos = std::string::npos;
            bool has_top_level_comma = false;
            for (std::size_t cursor = 0; cursor < body.size(); ++cursor) {
                const char ch = body[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < body.size()) {
                        ++cursor;
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
                if (ch == ',') {
                    has_top_level_comma = true;
                    break;
                }
                if (ch == '=' && equals_pos == std::string::npos) {
                    equals_pos = cursor;
                }
            }
            if (has_top_level_comma || equals_pos == std::string::npos) {
                return false;
            }

            const std::string key = std::string(refii::lua::TrimLuaWhitespace(
                body.substr(0, equals_pos)));
            const std::string value = std::string(refii::lua::TrimLuaWhitespace(
                body.substr(equals_pos + 1)));
            if (!refii::lua::IsValidLuaIdentifier(key) || value.empty()) {
                return false;
            }

            *key_out = key;
            *value_out = value;
            return true;
        };

    auto apply_function_range_rewrite =
        [&](std::size_t function_start,
            std::size_t function_end,
            bool* changed_target_function_out) {
            if (function_start >= lines.size() || function_end >= lines.size() ||
                function_start >= function_end) {
                return false;
            }
            if (function_end - function_start > 24) {
                return false;
            }

            bool changed_any = false;
            for (std::size_t line_index = function_start + 1; line_index < function_end;
                 ++line_index) {
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment(lines[line_index]));
                if (trimmed.empty()) {
                    continue;
                }
                if (line_index != function_start &&
                    refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    const std::size_t nested_end =
                        refii::lua::FindRenderedFunctionBlockEnd(lines, line_index);
                    if (nested_end >= lines.size() || nested_end <= line_index) {
                        break;
                    }
                    line_index = nested_end;
                    continue;
                }

                bool is_local = false;
                std::vector<std::string> lhs_names;
                std::string rhs;
                std::string lhs;
                if (refii::lua::ParseRenderedAssignmentLine(lines[line_index],
                                                            &is_local,
                                                            &lhs_names,
                                                            &rhs) &&
                    !is_local && lhs_names.size() == 1) {
                    lhs = lhs_names.front();
                } else {
                    continue;
                }

                const std::string lhs_trimmed =
                    std::string(refii::lua::TrimLuaWhitespace(lhs));
                const bool is_group_members_shape =
                    lhs_trimmed.find(".GroupMembers[") != std::string::npos ||
                    lhs_trimmed.find(".groupmembers[") != std::string::npos;
                if (!is_group_members_shape) {
                    continue;
                }

                std::string field_key;
                std::string field_value;
                if (!parse_singleton_table_field(rhs, &field_key, &field_value)) {
                    continue;
                }

                const std::string rewritten =
                    GetLuaLineIndent(lines[line_index]) + lhs_trimmed + "." + field_key +
                    " = " + field_value;
                if (rewritten == lines[line_index]) {
                    continue;
                }
                lines[line_index] = rewritten;
                changed_any = true;
                if (changed_target_function_out != nullptr &&
                    line_index >= target_function_start &&
                    line_index <= target_function_end) {
                    *changed_target_function_out = true;
                }
            }

            return changed_any;
        };

    bool changed = false;
    bool changed_target_function = false;
    for (std::size_t function_index = 1;; ++function_index) {
        const refii::lua::Lua51Function* function = GetLuaFunctionByPreorderIndex(
            context.compiled_candidate.chunk, function_index);
        if (function == nullptr) {
            break;
        }
        if (function->line_defined <= 0 || function->last_line_defined <= 0) {
            continue;
        }
        const std::size_t function_start =
            static_cast<std::size_t>(function->line_defined - 1);
        const std::size_t function_end =
            static_cast<std::size_t>(function->last_line_defined - 1);
        if (apply_function_range_rewrite(function_start,
                                         function_end,
                                         &changed_target_function)) {
            changed = true;
        }
    }

    if (!changed || !changed_target_function) {
        return false;
    }

    candidate_context.MarkLinesMutated();
    const std::string transformed_source =
        std::string(candidate_context.EnsureSource());
    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(
            transformed_source,
            "bytecode_parity_candidate_groupmembers_singleton",
            &transformed_chunk) ||
        !transformed_chunk.chunk_parsed) {
        return false;
    }

    if (!Lua51FunctionBytecodeExactInChunk(context.original_chunk,
                                           transformed_chunk.chunk,
                                           context.mismatch.function_index)) {
        return false;
    }

    const std::vector<std::size_t> candidate_mismatch_indices =
        CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                            context.compiled_candidate.chunk);
    const std::vector<std::size_t> transformed_mismatch_indices =
        CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                            transformed_chunk.chunk);
    if (transformed_mismatch_indices.size() > candidate_mismatch_indices.size()) {
        return false;
    }

    *candidate_source = transformed_source;
    return true;
}
