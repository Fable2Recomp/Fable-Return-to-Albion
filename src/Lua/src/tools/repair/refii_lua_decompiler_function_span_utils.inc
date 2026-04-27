bool TryFindFunctionSpanByPrefix(const std::vector<std::string>& lines,
                                 std::string_view assigned_prefix,
                                 DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || assigned_prefix.empty()) {
        return false;
    }

    const std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        DecompiledFunctionSpan header_span;
        header_span.start_line = line_index;
        std::string header_prefix;
        if (!TryExtractAssignedPrefixFromSpanHeader(lines, header_span, &header_prefix) ||
            header_prefix != assigned_prefix) {
            continue;
        }
        for (const DecompiledFunctionSpan& span : spans) {
            if (span.start_line == line_index) {
                *span_out = span;
                return true;
            }
        }
        if (TryExtractFunctionSpanFromStartLine(lines, line_index, span_out)) {
            return true;
        }
    }
    return TryFindFunctionSpanByTemporaryAssignmentPrefix(lines, assigned_prefix, span_out);
}

bool TryResolveFunctionSpanForIndex(const refii::lua::Lua51Function& main_function,
                                    std::size_t preorder_function_index,
                                    std::size_t root_child_index,
                                    const std::vector<std::string>& lines,
                                    const std::vector<DecompiledFunctionSpan>& spans,
                                    const std::unordered_map<std::string, DecompiledFunctionSpan>*
                                        prefix_span_map,
                                    DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || preorder_function_index == 0) {
        return false;
    }

    std::string assigned_prefix;
    if (root_child_index != 0 &&
        TryInferClosureAssignmentPrefix(main_function, root_child_index, &assigned_prefix)) {
        DecompiledFunctionSpan annotated_span;
        bool have_annotated_span = false;
        if (prefix_span_map != nullptr) {
            const auto found = prefix_span_map->find(assigned_prefix);
            if (found != prefix_span_map->end()) {
                annotated_span = found->second;
                have_annotated_span = true;
            }
        }
        DecompiledFunctionSpan expanded_span;
        if (TryExtractFunctionSpanByHeaderPrefix(lines, assigned_prefix, &expanded_span)) {
            if (have_annotated_span) {
                expanded_span.top_level =
                    expanded_span.top_level || annotated_span.top_level;
                expanded_span.has_error =
                    expanded_span.has_error || annotated_span.has_error;
                expanded_span.looks_suspicious =
                    expanded_span.looks_suspicious || annotated_span.looks_suspicious;
            }
            *span_out = std::move(expanded_span);
            return true;
        }
        if (TryExtractTopLevelFunctionSpanByPrefixUntilNextBoundary(lines,
                                                                    assigned_prefix,
                                                                    &expanded_span)) {
            if (have_annotated_span) {
                expanded_span.top_level =
                    expanded_span.top_level || annotated_span.top_level;
                expanded_span.has_error =
                    expanded_span.has_error || annotated_span.has_error;
                expanded_span.looks_suspicious =
                    expanded_span.looks_suspicious || annotated_span.looks_suspicious;
            }
            *span_out = std::move(expanded_span);
            return true;
        }
        if (have_annotated_span) {
            *span_out = std::move(annotated_span);
            return true;
        }
        DecompiledFunctionSpan exact_span;
        if (TryFindFunctionSpanByPrefix(lines, assigned_prefix, &exact_span)) {
            *span_out = std::move(exact_span);
            return true;
        }
    }

    if (preorder_function_index <= spans.size()) {
        *span_out = spans[preorder_function_index - 1];
        return true;
    }

    return false;
}

bool TryFindFunctionSpanByTemporaryAssignmentPrefix(
    const std::vector<std::string>& lines,
    std::string_view assigned_prefix,
    DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || assigned_prefix.empty()) {
        return false;
    }

    const std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    if (spans.empty()) {
        return false;
    }

    const auto resolve_temp_based_lhs_near_header =
        [&](std::size_t header_line,
            std::string_view expression,
            std::string* resolved_out) -> bool {
        if (resolved_out == nullptr) {
            return false;
        }

        expression = TrimWhitespace(expression);
        if (expression.empty()) {
            return false;
        }

        std::size_t identifier_end = 0;
        while (identifier_end < expression.size() &&
               IsIdentifierChar(expression[identifier_end])) {
            ++identifier_end;
        }
        if (identifier_end == 0) {
            return false;
        }

        const std::string_view base = expression.substr(0, identifier_end);
        if (!IsLuaIdentifier(base)) {
            return false;
        }
        const std::string_view suffix = expression.substr(identifier_end);

        constexpr std::size_t kAliasSearchLimit = 8;
        std::size_t searched = 0;
        for (std::size_t probe = header_line; probe-- > 0 && searched < kAliasSearchLimit;) {
            if (TrimWhitespace(StripLineComment(lines[probe])).empty()) {
                continue;
            }
            ++searched;

            std::string indent;
            std::string lhs;
            std::string rhs;
            if (!ParseSimpleRenderedAssignment(lines[probe], &indent, &lhs, &rhs)) {
                continue;
            }
            if (lhs != base) {
                continue;
            }

            *resolved_out = rhs + std::string(suffix);
            return true;
        }
        return false;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string indent;
        std::string assignment_lhs;
        std::string assignment_rhs;
        if (!ParseSimpleRenderedAssignment(lines[line_index],
                                           &indent,
                                           &assignment_lhs,
                                           &assignment_rhs)) {
            continue;
        }
        if (!IsLuaIdentifier(assignment_rhs)) {
            continue;
        }

        for (std::size_t header_line = line_index; header_line-- > 0;) {
            std::string header_indent;
            std::string header_lhs;
            std::string header_rhs;
            if (!ParseSimpleRenderedAssignment(lines[header_line],
                                               &header_indent,
                                               &header_lhs,
                                               &header_rhs)) {
                continue;
            }
            if (header_lhs != assignment_rhs) {
                continue;
            }
            if (!StartsWith(TrimWhitespace(header_rhs), "function")) {
                continue;
            }

            std::string resolved_lhs = assignment_lhs;
            if (resolved_lhs != assigned_prefix &&
                !resolve_temp_based_lhs_near_header(header_line,
                                                    assignment_lhs,
                                                    &resolved_lhs)) {
                continue;
            }
            if (resolved_lhs != assigned_prefix) {
                continue;
            }

            for (const DecompiledFunctionSpan& span : spans) {
                if (span.start_line == header_line) {
                    *span_out = span;
                    return true;
                }
            }

            if (TryExtractFunctionSpanFromStartLine(lines, header_line, span_out)) {
                return true;
            }
            return false;
        }
    }

    return false;
}

std::vector<DecompiledFunctionSpan> CollectDecompiledFunctionSpans(
    const std::vector<std::string>& lines) {
    std::vector<DecompiledFunctionSpan> spans;
    std::vector<LuaTextBlock> block_stack;
    std::size_t root_indent_size = std::numeric_limits<std::size_t>::max();
    for (const std::string& line : lines) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (trimmed.empty()) {
            continue;
        }
        root_indent_size = std::min(root_indent_size, GetLuaLeadingIndent(line).size());
    }
    if (root_indent_size == std::numeric_limits<std::size_t>::max()) {
        root_indent_size = 0;
    }
    const auto advance_brace_depth = [](std::string_view line,
                                        std::size_t limit,
                                        int depth) -> int {
        limit = std::min(limit, line.size());
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        for (std::size_t i = 0; i < limit; ++i) {
            const char ch = line[i];
            if (!in_single_quote && !in_double_quote && !escaped &&
                ch == '-' && i + 1 < limit && line[i + 1] == '-') {
                break;
            }

            if (escaped) {
                escaped = false;
                continue;
            }

            if (in_single_quote) {
                if (ch == '\\') {
                    escaped = true;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }

            if (in_double_quote) {
                if (ch == '\\') {
                    escaped = true;
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

            if (ch == '{') {
                ++depth;
            } else if (ch == '}') {
                depth = std::max(0, depth - 1);
            }
        }
        return depth;
    };
    const auto span_has_unclosed_blocks = [&](std::size_t start_line,
                                              std::size_t end_line) -> bool {
        int end_balance = 0;
        int until_balance = 0;
        for (std::size_t index = start_line; index <= end_line && index < lines.size(); ++index) {
            ForEachLuaToken(lines[index], [&](std::string_view token, std::size_t, std::size_t) {
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
        }
        return end_balance > 0 || until_balance > 0;
    };
    const auto find_previous_significant_line = [&](std::size_t start_line) {
        for (std::size_t probe = start_line; probe-- > 0;) {
            if (!TrimWhitespace(StripLineComment(lines[probe])).empty()) {
                return probe;
            }
        }
        return lines.size();
    };

    int table_brace_depth = 0;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string& line = lines[line_index];
        const int brace_depth_before_line = table_brace_depth;
        ForEachLuaToken(line, [&](std::string_view token, std::size_t begin, std::size_t end) {
            if (token == "function") {
                const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
                const std::size_t current_indent_size = GetLuaLeadingIndent(line).size();
                const bool assigned_function_header =
                    trimmed.find("= function(") != std::string_view::npos ||
                    trimmed.find("= function ") != std::string_view::npos ||
                    StartsWith(trimmed, "function(") ||
                    StartsWith(trimmed, "function ") ||
                    StartsWith(trimmed, "local function ");
                DecompiledFunctionSpan span;
                span.start_line = line_index;
                span.end_line = line_index;
                span.prefix = line.substr(0, begin);
                const bool no_enclosing_function = std::none_of(
                    block_stack.begin(), block_stack.end(), [](const LuaTextBlock& block) {
                        return block.kind == LuaTextBlockKind::kFunction;
                    });
                const std::size_t first_non_space = line.find_first_not_of(" \t");
                const bool starts_at_root_indent =
                    first_non_space != std::string::npos &&
                    GetLuaLeadingIndent(line).size() == root_indent_size;
                const bool nested_in_table_literal =
                    advance_brace_depth(line, begin, brace_depth_before_line) > 0;
                bool probable_top_level_sibling = false;
                if (!no_enclosing_function && !nested_in_table_literal &&
                    assigned_function_header &&
                    current_indent_size <= root_indent_size + 2) {
                    const std::size_t previous = find_previous_significant_line(line_index);
                    if (previous < lines.size()) {
                        const std::string_view previous_trimmed =
                            TrimWhitespace(StripLineComment(lines[previous]));
                        probable_top_level_sibling =
                            previous_trimmed == "end" || previous_trimmed == "}" ||
                            LooksLikeTopLevelBoundary(previous_trimmed);
                    }
                }
                if (probable_top_level_sibling) {
                    const std::size_t previous = find_previous_significant_line(line_index);
                    while (!block_stack.empty()) {
                        const LuaTextBlock block = block_stack.back();
                        block_stack.pop_back();
                        if (block.kind != LuaTextBlockKind::kFunction ||
                            block.span_index >= spans.size()) {
                            continue;
                        }
                        DecompiledFunctionSpan& enclosing_span = spans[block.span_index];
                        enclosing_span.looks_suspicious = true;
                        enclosing_span.has_error = true;
                        if (previous < lines.size() && previous >= enclosing_span.start_line) {
                            enclosing_span.end_line = previous;
                            enclosing_span.suffix.clear();
                        }
                        break;
                    }
                }
                span.top_level =
                    (no_enclosing_function || starts_at_root_indent ||
                     probable_top_level_sibling) &&
                    !nested_in_table_literal;
                spans.push_back(std::move(span));
                block_stack.push_back({LuaTextBlockKind::kFunction, spans.size() - 1});
                return;
            }

            if (token == "repeat") {
                block_stack.push_back({LuaTextBlockKind::kRepeat, 0});
                return;
            }

            if (token == "if" || token == "for" || token == "while" || token == "do") {
                block_stack.push_back({LuaTextBlockKind::kBlock, 0});
                return;
            }

            if (token == "end") {
                while (!block_stack.empty() &&
                       block_stack.back().kind == LuaTextBlockKind::kRepeat) {
                    block_stack.pop_back();
                }
                if (block_stack.empty()) {
                    return;
                }
                const LuaTextBlock block = block_stack.back();
                block_stack.pop_back();
                if (block.kind == LuaTextBlockKind::kFunction &&
                    block.span_index < spans.size()) {
                    spans[block.span_index].end_line = line_index;
                    spans[block.span_index].suffix = line.substr(end);
                }
                return;
            }

            if (token == "until") {
                while (!block_stack.empty() &&
                       block_stack.back().kind != LuaTextBlockKind::kRepeat) {
                    block_stack.pop_back();
                }
                if (!block_stack.empty() &&
                    block_stack.back().kind == LuaTextBlockKind::kRepeat) {
                    block_stack.pop_back();
                }
            }
        });
        table_brace_depth = advance_brace_depth(line, line.size(), table_brace_depth);

        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        const bool line_looks_broken =
            line.find("DECOMPILER ERROR:") != std::string::npos ||
            line.find("decompiler aborted repeated control-flow") != std::string::npos ||
            line.find("Warning: missing end") != std::string::npos ||
            line.find("(for generator)") != std::string::npos ||
            line.find("nil[") != std::string::npos ||
            line.find("nil(") != std::string::npos ||
            line.find("nil.") != std::string::npos ||
            ContainsNilArithmeticPattern(trimmed) ||
            trimmed == "local";
        const bool line_looks_suspicious =
            line_looks_broken || trimmed == "while true do" || trimmed == "do return end" ||
            trimmed == "until true";
        if (line_looks_suspicious) {
            for (auto it = block_stack.rbegin(); it != block_stack.rend(); ++it) {
                if (it->kind == LuaTextBlockKind::kFunction && it->span_index < spans.size()) {
                    spans[it->span_index].looks_suspicious = true;
                    if (line_looks_broken) {
                        spans[it->span_index].has_error = true;
                    }
                    break;
                }
            }
        }
    }

    std::vector<std::size_t> top_level_starts;
    top_level_starts.reserve(spans.size());
    for (std::size_t index = 0; index < spans.size(); ++index) {
        if (spans[index].top_level) {
            top_level_starts.push_back(index);
        }
    }

    for (std::size_t order = 0; order < top_level_starts.size(); ++order) {
        DecompiledFunctionSpan& span = spans[top_level_starts[order]];
        if (span.end_line == span.start_line &&
            span.start_line < lines.size() &&
            !HasInlineFunctionEnd(lines[span.start_line])) {
            span.looks_suspicious = true;
            span.has_error = true;
        }
        if (!span.looks_suspicious) {
            continue;
        }

        std::size_t next_start_line = lines.size();
        if (order + 1 < top_level_starts.size()) {
            next_start_line = spans[top_level_starts[order + 1]].start_line;
        }
        const std::size_t inspection_end =
            next_start_line > 0 ? std::min(span.end_line, next_start_line - 1) : span.end_line;
        if (inspection_end >= span.start_line &&
            span_has_unclosed_blocks(span.start_line, inspection_end)) {
            span.looks_suspicious = true;
            span.has_error = true;
        }
        if (next_start_line != 0 && next_start_line <= span.end_line + 1) {
            span.looks_suspicious = true;
            span.has_error = true;
            if (next_start_line > 0) {
                std::size_t overlap_end = next_start_line - 1;
                while (overlap_end > span.start_line &&
                       TrimWhitespace(lines[overlap_end]).empty()) {
                    --overlap_end;
                }
                span.end_line = overlap_end;
                span.suffix.clear();
            }
        }
        if (next_start_line == 0) {
            continue;
        }

        std::size_t heuristic_end = next_start_line - 1;
        while (heuristic_end > span.start_line &&
               TrimWhitespace(lines[heuristic_end]).empty()) {
            --heuristic_end;
        }

        for (std::size_t probe = next_start_line; probe > span.end_line + 1; --probe) {
            const std::size_t line_index = probe - 1;
            if (TrimWhitespace(lines[line_index]).empty()) {
                if (line_index > span.start_line) {
                    heuristic_end = line_index - 1;
                }
                break;
            }
        }

        if (heuristic_end > span.end_line) {
            span.end_line = heuristic_end;
            span.suffix.clear();
        }
    }

    for (DecompiledFunctionSpan& span : spans) {
        if (span.start_line > span.end_line || span.end_line >= lines.size()) {
            continue;
        }
        const std::string_view start_trimmed =
            TrimWhitespace(StripLineComment(lines[span.start_line]));
        if (start_trimmed.find("= function") != std::string_view::npos ||
            StartsWith(start_trimmed, "function(") ||
            StartsWith(start_trimmed, "function ")) {
            std::size_t expanded_start = span.start_line;
            int tail_line_count = 0;
            while (expanded_start > 0 && tail_line_count < 4) {
                const std::size_t previous = expanded_start - 1;
                const std::string_view previous_trimmed =
                    TrimWhitespace(StripLineComment(lines[previous]));
                if (previous_trimmed.empty()) {
                    expanded_start = previous;
                    continue;
                }
                if (!LooksLikeOrphanClosureTailLine(previous_trimmed)) {
                    break;
                }
                expanded_start = previous;
                ++tail_line_count;
            }
            if (expanded_start < span.start_line) {
                span.start_line = expanded_start;
                span.looks_suspicious = true;
                span.has_error = true;
            }
        }
        if (LooksLikeBrokenMultiResultLocalSequence(lines, span.start_line, span.end_line)) {
            span.looks_suspicious = true;
            span.has_error = true;
        }
        if (LooksLikeDegenerateIfElseSequence(lines, span.start_line, span.end_line)) {
            span.looks_suspicious = true;
            span.has_error = true;
        }
        for (std::size_t line_index = span.start_line;
             line_index <= span.end_line && line_index < lines.size();
             ++line_index) {
            const std::string_view stripped = StripLineComment(lines[line_index]);
            if (stripped.find("nil[") != std::string_view::npos ||
                stripped.find("nil(") != std::string::npos ||
                stripped.find("nil.") != std::string::npos ||
                stripped.find("nil:") != std::string::npos ||
                stripped.find("math.max(nil") != std::string::npos ||
                stripped.find("math.min(nil") != std::string::npos ||
                ContainsNilArithmeticPattern(stripped)) {
                span.looks_suspicious = true;
                span.has_error = true;
                break;
            }
        }
    }

    return spans;
}

void AppendLuaFunctionChildrenPreorder(const refii::lua::Lua51Function& function,
                                       std::vector<const refii::lua::Lua51Function*>* out) {
    for (const refii::lua::Lua51Function& child : function.child_functions) {
        out->push_back(&child);
        AppendLuaFunctionChildrenPreorder(child, out);
    }
}

void AppendLuaFunctionChildrenPreorderWithRootChildIndex(
    const refii::lua::Lua51Function& function,
    std::size_t inherited_root_child_index,
    std::vector<const refii::lua::Lua51Function*>* functions_out,
    std::vector<std::size_t>* root_child_indices_out) {
    if (functions_out == nullptr || root_child_indices_out == nullptr) {
        return;
    }

    for (std::size_t child_index = 0; child_index < function.child_functions.size(); ++child_index) {
        const refii::lua::Lua51Function& child = function.child_functions[child_index];
        const std::size_t root_child_index =
            inherited_root_child_index != 0 ? inherited_root_child_index : (child_index + 1);
        functions_out->push_back(&child);
        root_child_indices_out->push_back(root_child_index);
        AppendLuaFunctionChildrenPreorderWithRootChildIndex(child,
                                                            root_child_index,
                                                            functions_out,
                                                            root_child_indices_out);
    }
}

std::size_t ExtractLuaOpenConstructLine(std::string_view validation_error) {
    constexpr std::string_view kNeedle = "to close '";
    const std::size_t needle = validation_error.find(kNeedle);
    if (needle == std::string_view::npos) {
        return 0;
    }

    const std::size_t construct_end =
        validation_error.find("' at line ", needle + kNeedle.size());
    if (construct_end == std::string_view::npos) {
        return 0;
    }

    const std::size_t number_begin = construct_end + std::string_view("' at line ").size();
    std::size_t number_end = number_begin;
    while (number_end < validation_error.size() &&
           std::isdigit(static_cast<unsigned char>(validation_error[number_end])) != 0) {
        ++number_end;
    }
    if (number_end == number_begin) {
        return 0;
    }

    return static_cast<std::size_t>(std::strtoul(
        std::string(validation_error.substr(number_begin, number_end - number_begin)).c_str(),
        nullptr,
        10));
}
