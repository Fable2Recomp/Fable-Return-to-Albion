bool TryRepairTinyMainCreateSubClassSeedOwnership(const BytecodeParityRepairContext& context,
                                                  std::string* candidate_source) {
    if (candidate_source == nullptr ||
        std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) != 0) {
        return false;
    }
    if (Lua51FunctionBytecodeExact(context.original_chunk.main,
                                   context.compiled_candidate.chunk.main)) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
    std::vector<std::string>* lines = candidate_context.EnsureMutableLines();
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = NormalizeRenderedLuaNearExactCreateSubClassSeeds(lines);
    if (!changed) {
        for (std::string& line : *lines) {
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment(line));
            if (trimmed.empty()) {
                continue;
            }

            const std::size_t equals_pos = trimmed.find('=');
            const std::size_t call_pos = trimmed.find(":CreateSubClass(");
            if (equals_pos == std::string_view::npos || call_pos == std::string_view::npos ||
                equals_pos >= call_pos) {
                continue;
            }

            const std::string lhs = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
            if (!refii::lua::IsValidLuaIdentifier(lhs)) {
                continue;
            }

            const std::string rhs = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
            const std::string call_suffix = ":CreateSubClass(\"" + lhs + "\")";
            if (!EndsWith(rhs, call_suffix)) {
                continue;
            }

            const std::string receiver = std::string(refii::lua::TrimLuaWhitespace(
                rhs.substr(0, rhs.size() - call_suffix.size())));
            if (receiver.empty()) {
                continue;
            }

            line = GetLuaLineIndent(line) + rhs;
            changed = true;
        }
    }
    if (!changed) {
        for (std::string& line : *lines) {
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment(line));
            if (trimmed.empty() || trimmed.find('=') != std::string_view::npos) {
                continue;
            }

            const std::size_t call_pos = trimmed.find(":CreateSubClass(");
            if (call_pos == std::string_view::npos || call_pos == 0) {
                continue;
            }
            const std::string receiver = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(0, call_pos)));
            if (receiver.empty() || receiver.find('[') != std::string::npos ||
                receiver.find(':') != std::string::npos) {
                continue;
            }

            const std::size_t arg_start = call_pos + std::string(":CreateSubClass(").size();
            if (arg_start >= trimmed.size() || trimmed.back() != ')') {
                continue;
            }
            const std::string_view arg = refii::lua::TrimLuaWhitespace(
                trimmed.substr(arg_start, trimmed.size() - arg_start - 1));
            if (arg.size() < 2) {
                continue;
            }
            const char quote = arg.front();
            if ((quote != '"' && quote != '\'') || arg.back() != quote) {
                continue;
            }
            const std::string subclass_name(arg.substr(1, arg.size() - 2));
            if (!refii::lua::IsValidLuaIdentifier(subclass_name)) {
                continue;
            }

            line = GetLuaLineIndent(line) + subclass_name + " = " + std::string(trimmed);
            changed = true;
        }
    }
    if (!changed) {
        return false;
    }

    candidate_context.MarkLinesMutated();
    const std::string transformed_source =
        std::string(candidate_context.EnsureSource());
    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(
            transformed_source,
            "bytecode_parity_candidate_tiny_main_seed_ownership",
            &transformed_chunk) ||
        !transformed_chunk.chunk_parsed) {
        return false;
    }

    if (!Lua51FunctionBytecodeExact(context.original_chunk.main,
                                    transformed_chunk.chunk.main)) {
        return false;
    }

    const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
        refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                     transformed_chunk.chunk);
    if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch)) {
        return false;
    }

    *candidate_source = transformed_source;
    return true;
}

bool TryRepairTinyMainSingletonIndexTableAssignments(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr ||
        std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) != 0) {
        return false;
    }
    if (Lua51FunctionBytecodeExact(context.original_chunk.main,
                                   context.compiled_candidate.chunk.main)) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
    std::vector<std::string>* lines = candidate_context.EnsureMutableLines();
    if (lines == nullptr || lines->empty() ||
        !NormalizeRenderedLuaNearExactSingletonIndexTableAssignments(lines)) {
        return false;
    }

    candidate_context.MarkLinesMutated();
    const std::string transformed_source =
        std::string(candidate_context.EnsureSource());
    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(
            transformed_source,
            "bytecode_parity_candidate_tiny_main_singleton_index_exact",
            &transformed_chunk) ||
        !transformed_chunk.chunk_parsed) {
        return false;
    }

    if (!Lua51FunctionBytecodeExact(context.original_chunk.main,
                                    transformed_chunk.chunk.main)) {
        return false;
    }

    const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
        refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                     transformed_chunk.chunk);
    if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch)) {
        return false;
    }

    *candidate_source = transformed_source;
    return true;
}

bool TryRepairTinyMainRepeatedGlobalFieldWriteAlias(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr ||
        context.mismatch.function_index > 1 ||
        std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }
    if (Lua51FunctionBytecodeExact(context.original_chunk.main,
                                   context.compiled_candidate.chunk.main)) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
    std::vector<std::string>* lines = candidate_context.EnsureMutableLines();
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    std::size_t first_function_line = lines->size();
    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed.starts_with("function ") || trimmed.starts_with("local function ") ||
            trimmed.find("= function(") != std::string_view::npos) {
            first_function_line = index;
            break;
        }
    }
    if (first_function_line == 0 || first_function_line > lines->size()) {
        return false;
    }

    const auto parse_global_field_lhs =
        [](std::string_view line,
           std::string* root_identifier_out,
           std::string* field_suffix_out) -> bool {
            if (root_identifier_out == nullptr || field_suffix_out == nullptr) {
                return false;
            }
            const std::string_view stripped =
                refii::lua::StripLuaLineComment(line);
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(stripped);
            if (trimmed.empty() || trimmed.starts_with("local ")) {
                return false;
            }
            const std::size_t equals_pos = trimmed.find('=');
            if (equals_pos == std::string_view::npos) {
                return false;
            }
            const std::string_view lhs = refii::lua::TrimLuaWhitespace(
                trimmed.substr(0, equals_pos));
            const std::size_t dot_pos = lhs.find('.');
            if (dot_pos == std::string_view::npos || dot_pos == 0 || dot_pos + 1 >= lhs.size()) {
                return false;
            }
            if (lhs.find('[', dot_pos) != std::string_view::npos) {
                return false;
            }
            const std::string_view root_identifier = lhs.substr(0, dot_pos);
            if (!refii::lua::IsValidLuaIdentifier(root_identifier)) {
                return false;
            }
            const std::string_view field_suffix = lhs.substr(dot_pos + 1);
            if (field_suffix.empty()) {
                return false;
            }
            *root_identifier_out = std::string(root_identifier);
            *field_suffix_out = std::string(field_suffix);
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < first_function_line; ++line_index) {
        std::string root_identifier;
        std::string field_suffix;
        if (!parse_global_field_lhs((*lines)[line_index], &root_identifier, &field_suffix)) {
            continue;
        }

        std::size_t cluster_end = line_index;
        std::size_t cluster_count = 1;
        for (std::size_t probe = line_index + 1; probe < first_function_line; ++probe) {
            std::string probe_root_identifier;
            std::string probe_field_suffix;
            if (!parse_global_field_lhs((*lines)[probe],
                                        &probe_root_identifier,
                                        &probe_field_suffix) ||
                probe_root_identifier != root_identifier) {
                break;
            }
            cluster_end = probe;
            ++cluster_count;
        }
        if (cluster_count < 2) {
            continue;
        }

        std::string alias_identifier = "refii_main_alias_0";
        {
            std::size_t alias_index = 0;
            auto alias_in_use = [&](std::string_view alias) {
                for (const std::string& line : *lines) {
                    if (line.find(alias) != std::string::npos) {
                        return true;
                    }
                }
                return false;
            };
            while (alias_in_use(alias_identifier)) {
                ++alias_index;
                alias_identifier = "refii_main_alias_" + std::to_string(alias_index);
            }
        }

        const std::string alias_indent = GetLuaLineIndent((*lines)[line_index]);
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      alias_indent + "local " + alias_identifier + " = " +
                          root_identifier);
        ++line_index;
        ++cluster_end;
        ++first_function_line;

        for (std::size_t rewrite_index = line_index; rewrite_index <= cluster_end; ++rewrite_index) {
            const std::string& original_line = (*lines)[rewrite_index];
            const std::string_view stripped =
                refii::lua::StripLuaLineComment(original_line);
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(stripped);
            const std::size_t equals_pos = trimmed.find('=');
            if (equals_pos == std::string_view::npos) {
                continue;
            }
            const std::string lhs = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos)));
            const std::string rhs = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1)));
            const std::string root_prefix = root_identifier + ".";
            if (!StartsWith(lhs, root_prefix)) {
                continue;
            }

            const std::string rewritten_lhs =
                alias_identifier + "." + lhs.substr(root_prefix.size());
            (*lines)[rewrite_index] =
                GetLuaLineIndent(original_line) + rewritten_lhs + " = " + rhs;
        }
        changed = true;
        break;
    }

    if (!changed) {
        return false;
    }

    candidate_context.MarkLinesMutated();
    const std::string transformed_source =
        std::string(candidate_context.EnsureSource());
    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(
            transformed_source,
            "bytecode_parity_candidate_tiny_main_global_alias_exact",
            &transformed_chunk) ||
        !transformed_chunk.chunk_parsed) {
        return false;
    }

    if (!Lua51FunctionBytecodeExact(context.original_chunk.main,
                                    transformed_chunk.chunk.main)) {
        return false;
    }

    const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
        refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                     transformed_chunk.chunk);
    if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch)) {
        return false;
    }

    *candidate_source = transformed_source;
    return true;
}

bool TryRepairTinyMainTopLevelFieldSeedCluster(
    const BytecodeParityRepairContext& context,
    std::string* candidate_source) {
    if (candidate_source == nullptr ||
        context.mismatch.function_index > 1 ||
        std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 1) {
        return false;
    }
    if (Lua51FunctionBytecodeExact(context.original_chunk.main,
                                   context.compiled_candidate.chunk.main)) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
    std::vector<std::string>* lines = candidate_context.EnsureMutableLines();
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    std::size_t first_function_line = lines->size();
    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed.starts_with("function ") || trimmed.starts_with("local function ") ||
            trimmed.find("= function(") != std::string_view::npos) {
            first_function_line = index;
            break;
        }
    }
    if (first_function_line == 0 || first_function_line > lines->size()) {
        return false;
    }

    const auto parse_assignment_line =
        [&](std::string_view line,
            bool* is_local_out,
            std::string* lhs_out,
            std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment(line));
            if (trimmed.empty()) {
                return false;
            }
            bool is_local = false;
            if (trimmed.starts_with("local ")) {
                is_local = true;
                trimmed = refii::lua::TrimLuaWhitespace(trimmed.substr(6));
            }

            const std::size_t equals_pos = trimmed.find('=');
            if (equals_pos == std::string_view::npos || equals_pos == 0 ||
                equals_pos + 1 >= trimmed.size()) {
                return false;
            }
            const char left = trimmed[equals_pos - 1];
            const char right = trimmed[equals_pos + 1];
            if (left == '=' || left == '<' || left == '>' || left == '~' ||
                right == '=') {
                return false;
            }

            const std::string_view lhs =
                refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals_pos));
            const std::string_view rhs =
                refii::lua::TrimLuaWhitespace(trimmed.substr(equals_pos + 1));
            if (lhs.empty() || rhs.empty() ||
                lhs.find(',') != std::string_view::npos) {
                return false;
            }

            *is_local_out = is_local;
            *lhs_out = std::string(lhs);
            *rhs_out = std::string(rhs);
            return !lhs_out->empty();
        };

    const auto is_safe_field_lhs = [](std::string_view lhs) {
        return lhs.find('.') != std::string_view::npos &&
               lhs.find('[') == std::string_view::npos &&
               lhs.find(':') == std::string_view::npos;
    };

    const auto try_commit = [&](const std::vector<std::string>& transformed_lines,
                                std::string_view compile_label) {
        const std::string transformed_source =
            JoinLines(transformed_lines, candidate_context.HadTrailingNewline());
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(transformed_source,
                                           std::string(compile_label).c_str(),
                                           &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            return false;
        }

        if (!Lua51FunctionBytecodeExact(context.original_chunk.main,
                                        transformed_chunk.chunk.main)) {
            return false;
        }

        const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
            refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                         transformed_chunk.chunk);
        if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch)) {
            return false;
        }

        *candidate_source = transformed_source;
        return true;
    };

    for (std::size_t line_index = 0; line_index + 1 < first_function_line; ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!parse_assignment_line((*lines)[line_index],
                                   &seed_is_local,
                                   &seed_lhs,
                                   &seed_rhs) ||
            seed_is_local || seed_rhs != "{}" ||
            !is_safe_field_lhs(seed_lhs)) {
            continue;
        }

        std::vector<std::size_t> cluster_lines;
        std::vector<std::pair<std::string, std::string>> cluster_entries;
        for (std::size_t probe = line_index + 1; probe < first_function_line; ++probe) {
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                if (!cluster_lines.empty()) {
                    break;
                }
                continue;
            }

            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (!parse_assignment_line((*lines)[probe],
                                       &probe_is_local,
                                       &probe_lhs,
                                       &probe_rhs) ||
                probe_is_local ||
                !StartsWith(probe_lhs, seed_lhs + "[") ||
                probe_rhs.empty()) {
                break;
            }

            const std::string key_suffix = probe_lhs.substr(seed_lhs.size());
            if (key_suffix.size() < 3 || key_suffix.front() != '[' ||
                key_suffix.back() != ']') {
                break;
            }

            cluster_lines.push_back(probe);
            cluster_entries.emplace_back(key_suffix, probe_rhs);
        }

        if (cluster_lines.empty()) {
            continue;
        }

        {
            std::vector<std::string> transformed_lines = *lines;
            std::string alias_name = "refii_seed_tmp_0";
            std::size_t alias_suffix = 0;
            auto alias_in_use = [&](std::string_view alias) {
                for (const std::string& line : transformed_lines) {
                    if (line.find(alias) != std::string::npos) {
                        return true;
                    }
                }
                return false;
            };
            while (alias_in_use(alias_name)) {
                ++alias_suffix;
                alias_name = "refii_seed_tmp_" + std::to_string(alias_suffix);
            }

            const std::string seed_indent = GetLuaLineIndent((*lines)[line_index]);
            transformed_lines[line_index] =
                seed_indent + "local " + alias_name + " = {}";
            for (const std::size_t probe : cluster_lines) {
                const std::string& original_line = transformed_lines[probe];
                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (!parse_assignment_line(original_line,
                                           &probe_is_local,
                                           &probe_lhs,
                                           &probe_rhs)) {
                    continue;
                }
                if (!StartsWith(probe_lhs, seed_lhs + "[")) {
                    continue;
                }
                transformed_lines[probe] = GetLuaLineIndent(original_line) +
                                           alias_name +
                                           probe_lhs.substr(seed_lhs.size()) +
                                           " = " + probe_rhs;
            }

            const std::size_t insertion_index = cluster_lines.back() + 1;
            transformed_lines.insert(
                transformed_lines.begin() + static_cast<std::ptrdiff_t>(insertion_index),
                seed_indent + seed_lhs + " = " + alias_name);
            if (try_commit(transformed_lines,
                           "bytecode_parity_candidate_tiny_main_field_seed_cluster_alias")) {
                return true;
            }
        }

        {
            std::vector<std::string> transformed_lines = *lines;
            std::string literal_body;
            for (std::size_t entry_index = 0; entry_index < cluster_entries.size();
                 ++entry_index) {
                if (!literal_body.empty()) {
                    literal_body += ", ";
                }
                literal_body += cluster_entries[entry_index].first + " = " +
                                cluster_entries[entry_index].second;
            }
            transformed_lines[line_index] =
                GetLuaLineIndent((*lines)[line_index]) + seed_lhs + " = {" +
                literal_body + "}";
            for (auto it = cluster_lines.rbegin(); it != cluster_lines.rend(); ++it) {
                transformed_lines.erase(transformed_lines.begin() +
                                        static_cast<std::ptrdiff_t>(*it));
            }
            if (try_commit(transformed_lines,
                           "bytecode_parity_candidate_tiny_main_field_seed_cluster_literal")) {
                return true;
            }
        }
    }

    return false;
}

bool TryRepairWhileConditionInitializerCallCarry(const BytecodeParityRepairContext& context,
                                                 std::string* candidate_source) {
    if (candidate_source == nullptr || context.mismatch.function_index == 0 ||
        context.mismatch.instruction_delta != -5 || context.mismatch.constant_delta != 0 ||
        context.mismatch.local_delta != 0) {
        return false;
    }

    const refii::lua::Lua51Function* function =
        GetLuaFunctionByPreorderIndex(context.compiled_candidate.chunk, context.mismatch.function_index);
    if (function == nullptr || function->line_defined == 0 || function->last_line_defined == 0) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
    const std::vector<std::string>& original_lines = candidate_context.EnsureLines();
    const bool had_trailing_newline = candidate_context.HadTrailingNewline();
    if (original_lines.empty()) {
        return false;
    }

    const std::size_t function_start = static_cast<std::size_t>(function->line_defined - 1);
    const std::size_t function_end = static_cast<std::size_t>(function->last_line_defined - 1);
    if (function_start >= original_lines.size() || function_end >= original_lines.size() ||
        function_start >= function_end) {
        return false;
    }

    for (std::size_t while_line = function_start + 1; while_line < function_end; ++while_line) {
        std::string while_indent;
        std::string while_lhs;
        std::string while_op;
        std::string while_rhs;
        if (!TryParseSimpleRenderedWhileComparison(original_lines[while_line],
                                                   &while_indent,
                                                   &while_lhs,
                                                   &while_op,
                                                   &while_rhs)) {
            continue;
        }

        bool has_else = false;
        std::size_t while_end = 0;
        if (!TryFindLuaRenderedBlockEnd(original_lines,
                                        while_line,
                                        function_end - 1,
                                        &while_end,
                                        &has_else)) {
            continue;
        }

        std::string alias_source;
        std::string refreshed_expression;
        for (std::size_t line_index = while_line; line_index > function_start + 1; --line_index) {
            std::string indent;
            std::string lhs;
            std::string rhs;
            if (!ParseSimpleRenderedAssignment(original_lines[line_index - 1], &indent, &lhs, &rhs)) {
                continue;
            }
            if (lhs != while_lhs || ExtractLeadingIdentifier(rhs) != rhs) {
                continue;
            }
            alias_source = rhs;
            for (std::size_t source_line = line_index - 1; source_line > function_start + 1; --source_line) {
                std::string source_indent;
                std::string source_lhs;
                std::string source_rhs;
                if (!ParseSimpleRenderedAssignment(original_lines[source_line - 1],
                                                   &source_indent,
                                                   &source_lhs,
                                                   &source_rhs)) {
                    continue;
                }
                if (source_lhs != alias_source) {
                    continue;
                }
                std::string callee;
                std::vector<std::string> arguments;
                if (!ParseSimpleRenderedCall(source_rhs, &callee, &arguments)) {
                    continue;
                }
                bool refreshed_in_loop = false;
                bool alias_reassigned_in_loop = false;
                for (std::size_t probe = while_line + 1; probe < while_end; ++probe) {
                    std::string probe_indent;
                    std::string probe_lhs;
                    std::string probe_rhs;
                    if (!ParseSimpleRenderedAssignment(original_lines[probe],
                                                      &probe_indent,
                                                      &probe_lhs,
                                                      &probe_rhs)) {
                        continue;
                    }
                    if (probe_lhs == alias_source && probe_rhs == source_rhs) {
                        refreshed_in_loop = true;
                    }
                    if (probe_lhs == while_lhs && probe_rhs == alias_source) {
                        alias_reassigned_in_loop = true;
                    }
                }
                if (!refreshed_in_loop || !alias_reassigned_in_loop) {
                    continue;
                }
                refreshed_expression = source_rhs;
                break;
            }
            if (!refreshed_expression.empty()) {
                break;
            }
        }

        if (refreshed_expression.empty()) {
            continue;
        }

        std::vector<std::string> transformed_lines = original_lines;
        transformed_lines[while_line] =
            while_indent + "while " + refreshed_expression + " " + while_op + " " + while_rhs + " do";
        const std::string transformed_source = JoinLines(transformed_lines, had_trailing_newline);

        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(transformed_source,
                                           "bytecode_parity_candidate_while",
                                           &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            continue;
        }

        if (!IsLuaFunctionStatsImproved(context.original_chunk,
                                        context.compiled_candidate.chunk,
                                        transformed_chunk.chunk,
                                        context.mismatch.function_index)) {
            continue;
        }

        *candidate_source = transformed_source;
        return true;
    }

    return false;
}

bool TryRepairLocalDiffParameterNameMismatch(const BytecodeParityRepairContext& context,
                                             std::string* candidate_source) {
    if (candidate_source == nullptr ||
        context.mismatch.instruction_delta != 0 || context.mismatch.constant_delta != 0 ||
        context.mismatch.local_delta != 0) {
        return false;
    }

    const std::string_view local_diff_note = ExtractLocalDiffMismatchNote(context.mismatch.note);
    if (local_diff_note.empty()) {
        return false;
    }
    constexpr std::string_view kPrefix = "local_diff idx=";

    const auto parse_local_diff_entry =
        [](std::string_view text, std::string* name_out) -> bool {
            if (name_out == nullptr) {
                return false;
            }
            const std::size_t at = text.find('@');
            if (at == std::string_view::npos || at == 0) {
                return false;
            }
            const std::string_view name = TrimWhitespace(text.substr(0, at));
            if (!refii::lua::IsValidLuaIdentifier(name) ||
                refii::lua::IsRenderTemporaryName(name)) {
                return false;
            }
            *name_out = std::string(name);
            return true;
        };

    const std::size_t index_begin = kPrefix.size();
    std::size_t index_end = index_begin;
    while (index_end < local_diff_note.size() &&
           std::isdigit(static_cast<unsigned char>(local_diff_note[index_end])) != 0) {
        ++index_end;
    }
    if (index_end == index_begin) {
        return false;
    }

    const std::string_view remaining = TrimWhitespace(local_diff_note.substr(index_end));
    const std::size_t arrow = remaining.find(" -> ");
    if (arrow == std::string_view::npos || arrow == 0 || arrow + 4 >= remaining.size()) {
        return false;
    }

    std::string expected_name;
    std::string candidate_name;
    if (!parse_local_diff_entry(remaining.substr(0, arrow), &expected_name) ||
        !parse_local_diff_entry(remaining.substr(arrow + 4), &candidate_name) ||
        expected_name == candidate_name || !StartsWith(candidate_name, "arg")) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
    std::vector<std::string>* lines = candidate_context.EnsureMutableLines();
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    for (std::size_t function_line = 0; function_line < lines->size(); ++function_line) {
        if (!refii::lua::IsRenderedFunctionStartLine((*lines)[function_line])) {
            continue;
        }
        const std::size_t function_end =
            refii::lua::FindRenderedFunctionBlockEnd(*lines, function_line);
        if (function_end >= lines->size() || function_end <= function_line) {
            continue;
        }

        std::vector<std::string> params;
        if (!ParseRenderedFunctionParameterNames((*lines)[function_line], &params) ||
            params.empty()) {
            continue;
        }
        if (std::find(params.begin(), params.end(), expected_name) != params.end() ||
            std::find(params.begin(), params.end(), candidate_name) == params.end()) {
            continue;
        }

        std::unordered_map<std::string, std::string> replacements = {
            {candidate_name, expected_name}};
        std::vector<std::string> transformed_lines = *lines;
        for (std::size_t line_index = function_line; line_index <= function_end; ++line_index) {
            if (line_index != function_line &&
                refii::lua::IsRenderedFunctionStartLine(transformed_lines[line_index])) {
                const std::size_t nested_end =
                    refii::lua::FindRenderedFunctionBlockEnd(transformed_lines, line_index);
                if (nested_end >= transformed_lines.size() || nested_end <= line_index) {
                    break;
                }
                line_index = nested_end;
                continue;
            }
            transformed_lines[line_index] =
                RewriteLuaIdentifiersWithMap(transformed_lines[line_index], replacements);
        }

        refii::lua::internal::LuaMutableTextContext transformed_context;
        transformed_context.SetLines(std::move(transformed_lines),
                                     candidate_context.HadTrailingNewline());
        const std::string transformed_source =
            std::string(transformed_context.EnsureSource());
        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(transformed_source,
                                           "bytecode_parity_candidate_localdiff",
                                           &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            continue;
        }
        if (!Lua51FunctionBytecodeExactInChunk(context.original_chunk,
                                               transformed_chunk.chunk,
                                               context.mismatch.function_index)) {
            continue;
        }

        *candidate_source = transformed_source;
        return true;
    }

    return false;
}
