bool TryRepairInstructionDiffFunctionFromReference(
    const BytecodeParityRepairContext& context,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix) {
    const bool tiny_instruction_or_local_stats_mismatch =
        std::llabs(context.mismatch.instruction_delta) <= 1 &&
        std::llabs(context.mismatch.constant_delta) == 0 &&
        std::llabs(context.mismatch.local_delta) <= 2;
    if (candidate_source == nullptr || !tiny_instruction_or_local_stats_mismatch ||
        (context.mismatch.note.find("instruction_diff") == std::string::npos &&
         context.mismatch.note.find("function_stats_differ") == std::string::npos)) {
        return false;
    }

    std::string source_name_hint;
    if (input_path != nullptr) {
        source_name_hint = NormalizePathString(input_path->generic_string());
    }

    std::vector<std::string> reference_sources;
    std::string rendered_reference_source;
    if (TryRenderNativeMainChunkCandidate(input_bytes,
                                          &rendered_reference_source,
                                          nullptr,
                                          true,
                                          "bytecode_instructiondiff_reference",
                                          source_name_hint)) {
        reference_sources.push_back(rendered_reference_source);
    }
    if (TryRenderNativeMainChunkCandidate(input_bytes,
                                          &rendered_reference_source,
                                          nullptr,
                                          false,
                                          "bytecode_instructiondiff_reference_raw",
                                          source_name_hint)) {
        if (reference_sources.empty() || reference_sources.back() != rendered_reference_source) {
            reference_sources.push_back(rendered_reference_source);
        }
    }
    if (reference_sources.empty()) {
        return false;
    }

    for (const std::string& reference_source : reference_sources) {
        std::string transformed_source = *candidate_source;
        refii::lua::audit::CompiledLuaChunkInfo compiled_reference;
        if (!CompileLuaSourceToChunkCached(reference_source,
                                           "bytecode_parity_instructiondiff_reference_chunk",
                                           &compiled_reference) ||
            !compiled_reference.chunk_parsed) {
            continue;
        }

        const std::vector<std::size_t> candidate_mismatch_indices =
            CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                                context.compiled_candidate.chunk);
        const std::vector<std::size_t> reference_mismatch_indices =
            CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                                compiled_reference.chunk);
        const auto contains_index = [](const std::vector<std::size_t>& values,
                                       std::size_t index) {
            return std::find(values.begin(), values.end(), index) != values.end();
        };
        const auto introduces_non_target_mismatch =
            [&](const std::vector<std::size_t>& candidate_indices,
                const std::vector<std::size_t>& replacement_indices,
                std::size_t target_index) {
                for (const std::size_t index : replacement_indices) {
                    if (index == target_index) {
                        continue;
                    }
                    if (!contains_index(candidate_indices, index)) {
                        return true;
                    }
                }
                return false;
            };

        const std::string local_note_prefix =
            note_prefix.empty() ? std::string("bytecode_instructiondiff")
                                : std::string(note_prefix) + "_bytecode_instructiondiff";
        bool transformed_by_span_repair =
            TryRepairCandidateMismatchedFunctionsFromReference(input_bytes,
                                                               reference_source,
                                                               &transformed_source,
                                                               note,
                                                               local_note_prefix,
                                                               false);
        if (!transformed_by_span_repair) {
            const std::size_t target_index = context.mismatch.function_index;
            const bool target_cleared = !contains_index(reference_mismatch_indices, target_index);
            const bool mismatch_count_not_worse =
                reference_mismatch_indices.size() <= candidate_mismatch_indices.size();
            const bool reference_improves_target =
                IsLuaFunctionStatsImproved(context.original_chunk,
                                           context.compiled_candidate.chunk,
                                           compiled_reference.chunk,
                                           target_index);
            if (!target_cleared || !mismatch_count_not_worse ||
                introduces_non_target_mismatch(candidate_mismatch_indices,
                                               reference_mismatch_indices,
                                               target_index) ||
                !reference_improves_target) {
                continue;
            }
            transformed_source = reference_source;
        }

        refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
        if (!CompileLuaSourceToChunkCached(
                transformed_source,
                "bytecode_parity_candidate_instruction_diff",
                &transformed_chunk) ||
            !transformed_chunk.chunk_parsed) {
            continue;
        }

        const std::vector<std::size_t> transformed_mismatch_indices =
            CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                                transformed_chunk.chunk);
        if (transformed_mismatch_indices.size() > candidate_mismatch_indices.size() ||
            introduces_non_target_mismatch(candidate_mismatch_indices,
                                           transformed_mismatch_indices,
                                           context.mismatch.function_index)) {
            continue;
        }

        if (context.mismatch.function_index == 0) {
            if (!Lua51FunctionBytecodeExact(context.original_chunk.main,
                                            transformed_chunk.chunk.main)) {
                continue;
            }
        } else if (!Lua51FunctionBytecodeExactInChunk(context.original_chunk,
                                                      transformed_chunk.chunk,
                                                      context.mismatch.function_index)) {
            continue;
        }

        *candidate_source = std::move(transformed_source);
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  (transformed_by_span_repair
                                       ? "_bytecode_instructiondiff_function_exact"
                                       : "_bytecode_instructiondiff_reference_source_exact"));
        }
        return true;
    }

    return false;
}

bool TryRepairTinyMainFunctionStatsFromReference(
    const BytecodeParityRepairContext& context,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix) {
    if (candidate_source == nullptr || context.mismatch.function_index != 0 ||
        std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 2 ||
        context.original_chunk.main.code.size() > 16384) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }

    std::string source_name_hint;
    if (input_path != nullptr) {
        source_name_hint = NormalizePathString(input_path->generic_string());
    }

    std::vector<std::string> reference_sources;
    std::string rendered_reference_source;
    if (TryRenderNativeMainChunkCandidate(input_bytes,
                                          &rendered_reference_source,
                                          nullptr,
                                          true,
                                          "bytecode_tiny_main_reference",
                                          source_name_hint)) {
        reference_sources.push_back(rendered_reference_source);
    }
    if (TryRenderNativeMainChunkCandidate(input_bytes,
                                          &rendered_reference_source,
                                          nullptr,
                                          false,
                                          "bytecode_tiny_main_reference_raw",
                                          source_name_hint)) {
        if (reference_sources.empty() || reference_sources.back() != rendered_reference_source) {
            reference_sources.push_back(rendered_reference_source);
        }
    }
    if (reference_sources.empty()) {
        return false;
    }

    for (std::string reference_source : reference_sources) {
        bool reference_had_trailing_newline = false;
        std::vector<std::string> reference_lines =
            SplitLines(reference_source, &reference_had_trailing_newline);
        if (!reference_lines.empty()) {
            const bool reference_changed = ApplyRenderedLuaNearExactInstructionCleanupBundle(
                &reference_lines, false, false, true);
            if (reference_changed) {
                const std::string cleaned_reference_source =
                    JoinLines(reference_lines, reference_had_trailing_newline);
                refii::lua::audit::CompiledLuaChunkInfo cleaned_reference_chunk;
                if (CompileLuaSourceToChunkCached(cleaned_reference_source,
                                                  "bytecode_parity_tiny_main_reference_cleaned",
                                                  &cleaned_reference_chunk) &&
                    cleaned_reference_chunk.chunk_parsed) {
                    reference_source = cleaned_reference_source;
                }
            }
        }

        refii::lua::audit::CompiledLuaChunkInfo compiled_reference;
        if (!CompileLuaSourceToChunkCached(reference_source,
                                           "bytecode_parity_tiny_main_reference_chunk",
                                           &compiled_reference) ||
            !compiled_reference.chunk_parsed) {
            continue;
        }

        const std::vector<std::size_t> candidate_mismatch_indices =
            CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                                context.compiled_candidate.chunk);
        const std::vector<std::size_t> reference_mismatch_indices =
            CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                                compiled_reference.chunk);
        const auto contains_index = [](const std::vector<std::size_t>& values,
                                       std::size_t index) {
            return std::find(values.begin(), values.end(), index) != values.end();
        };
        const auto introduces_non_target_mismatch =
            [&](const std::vector<std::size_t>& replacement_indices,
                std::size_t target_index) {
                for (const std::size_t index : replacement_indices) {
                    if (index == target_index) {
                        continue;
                    }
                    if (!contains_index(candidate_mismatch_indices, index)) {
                        return true;
                    }
                }
                return false;
            };

        if (contains_index(reference_mismatch_indices, 0) ||
            reference_mismatch_indices.size() > candidate_mismatch_indices.size() ||
            introduces_non_target_mismatch(reference_mismatch_indices, 0) ||
            !IsLuaFunctionStatsImproved(context.original_chunk,
                                        context.compiled_candidate.chunk,
                                        compiled_reference.chunk,
                                        0)) {
            continue;
        }

        *candidate_source = std::move(reference_source);
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_bytecode_tiny_main_reference_exact");
        }
        return true;
    }

    return false;
}

bool TryRepairTinyMainTopLevelFromReference(
    const BytecodeParityRepairContext& context,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix) {
    if (candidate_source == nullptr ||
        std::llabs(context.mismatch.instruction_delta) > 1 ||
        std::llabs(context.mismatch.constant_delta) != 0 ||
        std::llabs(context.mismatch.local_delta) > 2 ||
        context.original_chunk.main.code.size() > 16384) {
        return false;
    }
    if (Lua51FunctionBytecodeExact(context.original_chunk.main,
                                   context.compiled_candidate.chunk.main)) {
        return false;
    }
    if (context.mismatch.note.find("function_stats_differ") == std::string::npos &&
        context.mismatch.note.find("instruction_diff") == std::string::npos) {
        return false;
    }

    std::string source_name_hint;
    if (input_path != nullptr) {
        source_name_hint = NormalizePathString(input_path->generic_string());
    }

    std::vector<std::string> reference_sources;
    std::string rendered_reference_source;
    if (TryRenderNativeMainChunkCandidate(input_bytes,
                                          &rendered_reference_source,
                                          nullptr,
                                          true,
                                          "bytecode_tiny_main_top_level_reference",
                                          source_name_hint)) {
        reference_sources.push_back(rendered_reference_source);
    }
    if (TryRenderNativeMainChunkCandidate(input_bytes,
                                          &rendered_reference_source,
                                          nullptr,
                                          false,
                                          "bytecode_tiny_main_top_level_reference_raw",
                                          source_name_hint)) {
        if (reference_sources.empty() || reference_sources.back() != rendered_reference_source) {
            reference_sources.push_back(rendered_reference_source);
        }
    }
    if (reference_sources.empty()) {
        return false;
    }

    const std::vector<std::size_t> candidate_mismatch_indices =
        CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                            context.compiled_candidate.chunk);
    const auto contains_index = [](const std::vector<std::size_t>& values,
                                   std::size_t index) {
        return std::find(values.begin(), values.end(), index) != values.end();
    };
    for (const std::string& reference_source : reference_sources) {
        bool reference_had_trailing_newline = false;
        std::vector<std::string> reference_lines =
            SplitLines(reference_source, &reference_had_trailing_newline);
        refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
        const std::vector<std::string>& candidate_lines_view =
            candidate_context.EnsureLines();
        const bool candidate_had_trailing_newline =
            candidate_context.HadTrailingNewline();
        std::vector<std::string> candidate_lines(candidate_lines_view.begin(),
                                                 candidate_lines_view.end());
        if (reference_lines.empty() || candidate_lines.empty()) {
            continue;
        }
        const auto try_commit = [&](const std::vector<std::string>& transformed_lines,
                                    std::string_view compile_label,
                                    std::string_view note_suffix) {
            const std::string transformed_source =
                JoinLines(transformed_lines, candidate_had_trailing_newline);
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

            const std::vector<std::size_t> transformed_mismatch_indices =
                CollectLuaMismatchedFunctionIndices(context.original_chunk,
                                                    transformed_chunk.chunk);
            if (transformed_mismatch_indices.size() > candidate_mismatch_indices.size()) {
                return false;
            }
            for (const std::size_t mismatch_index : transformed_mismatch_indices) {
                if (!contains_index(candidate_mismatch_indices, mismatch_index)) {
                    return false;
                }
            }

            *candidate_source = std::move(transformed_source);
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note, std::string(note_prefix) + std::string(note_suffix));
            }
            return true;
        };

        std::size_t reference_first_function = reference_lines.size();
        std::size_t candidate_first_function = candidate_lines.size();
        for (std::size_t i = 0; i < reference_lines.size(); ++i) {
            if (refii::lua::IsRenderedFunctionStartLine(reference_lines[i])) {
                reference_first_function = i;
                break;
            }
        }
        for (std::size_t i = 0; i < candidate_lines.size(); ++i) {
            if (refii::lua::IsRenderedFunctionStartLine(candidate_lines[i])) {
                candidate_first_function = i;
                break;
            }
        }
        if (reference_first_function == 0 || reference_first_function >= reference_lines.size() ||
            candidate_first_function == 0 || candidate_first_function >= candidate_lines.size()) {
            continue;
        }

        {
            std::vector<std::string> transformed_lines = candidate_lines;
            transformed_lines.erase(transformed_lines.begin(),
                                    transformed_lines.begin() +
                                        static_cast<std::ptrdiff_t>(candidate_first_function));
            transformed_lines.insert(transformed_lines.begin(),
                                     reference_lines.begin(),
                                     reference_lines.begin() +
                                         static_cast<std::ptrdiff_t>(reference_first_function));
            if (try_commit(transformed_lines,
                           "bytecode_parity_tiny_main_top_level_candidate",
                           "_bytecode_tiny_main_top_level_exact")) {
                return true;
            }
        }

        const std::vector<DecompiledFunctionSpan> reference_spans =
            CollectDecompiledFunctionSpans(reference_lines);
        const std::vector<DecompiledFunctionSpan> candidate_spans =
            CollectDecompiledFunctionSpans(candidate_lines);
        if (!reference_spans.empty() && reference_spans.size() == candidate_spans.size()) {
            std::vector<std::string> transformed_lines;
            transformed_lines.reserve(candidate_lines.size());

            std::size_t reference_cursor = 0;
            for (std::size_t span_index = 0; span_index < reference_spans.size(); ++span_index) {
                const DecompiledFunctionSpan& reference_span = reference_spans[span_index];
                const DecompiledFunctionSpan& candidate_span = candidate_spans[span_index];
                if (reference_span.start_line > reference_span.end_line ||
                    candidate_span.start_line > candidate_span.end_line ||
                    reference_span.end_line >= reference_lines.size() ||
                    candidate_span.end_line >= candidate_lines.size()) {
                    transformed_lines.clear();
                    break;
                }

                if (reference_cursor > reference_span.start_line) {
                    transformed_lines.clear();
                    break;
                }
                transformed_lines.insert(
                    transformed_lines.end(),
                    reference_lines.begin() + static_cast<std::ptrdiff_t>(reference_cursor),
                    reference_lines.begin() +
                        static_cast<std::ptrdiff_t>(reference_span.start_line));
                transformed_lines.insert(
                    transformed_lines.end(),
                    candidate_lines.begin() +
                        static_cast<std::ptrdiff_t>(candidate_span.start_line),
                    candidate_lines.begin() +
                        static_cast<std::ptrdiff_t>(candidate_span.end_line + 1));

                reference_cursor = reference_span.end_line + 1;
            }

            if (!transformed_lines.empty() && reference_cursor <= reference_lines.size()) {
                transformed_lines.insert(
                    transformed_lines.end(),
                    reference_lines.begin() + static_cast<std::ptrdiff_t>(reference_cursor),
                    reference_lines.end());
                if (try_commit(transformed_lines,
                               "bytecode_parity_tiny_main_top_level_segment_graft",
                               "_bytecode_tiny_main_top_level_segment_graft_exact")) {
                    return true;
                }
            }
        }
    }
    return false;
}
