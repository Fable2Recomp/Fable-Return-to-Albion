bool SimplifyRenderedLuaInvalidEmptyStringMethodReceivers(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct CallLineView {
        std::string prefix;
        std::string suffix;
        std::string callee;
        std::vector<std::string> arguments;
    };

    const auto try_parse_assignment_candidate = [](std::string_view line,
                                                   std::string* lhs_out,
                                                   std::string* rhs_out) -> bool {
        if (lhs_out == nullptr || rhs_out == nullptr) {
            return false;
        }

        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine(line, &is_local, &lhs, &rhs) ||
            !IsValidLuaIdentifier(lhs)) {
            return false;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs), nullptr);
        if (normalized_rhs.empty() || !IsRenderedSafePrefixExpression(normalized_rhs)) {
            return false;
        }

        *lhs_out = std::move(lhs);
        *rhs_out = std::move(rhs);
        return true;
    };

    const auto try_parse_simple_local_name = [](std::string_view line,
                                                std::string* name_out) -> bool {
        if (name_out == nullptr) {
            return false;
        }

        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("local ")) {
            return false;
        }

        const std::string_view rest = TrimLuaWhitespace(trimmed.substr(6));
        if (rest.empty()) {
            return false;
        }

        const std::size_t equal = rest.find('=');
        const std::string_view candidate =
            TrimLuaWhitespace(equal == std::string_view::npos ? rest : rest.substr(0, equal));
        if (candidate.empty() || candidate.find(',') != std::string_view::npos ||
            !IsValidLuaIdentifier(candidate) || IsRenderTemporaryName(candidate)) {
            return false;
        }

        *name_out = std::string(candidate);
        return true;
    };

    const auto is_scope_visible_at_use =
        [](const std::vector<std::vector<std::size_t>>& scope_paths,
           std::size_t candidate_index,
           std::size_t use_index) {
            if (candidate_index >= scope_paths.size() || use_index >= scope_paths.size()) {
                return false;
            }
            const std::vector<std::size_t>& candidate_scope = scope_paths[candidate_index];
            const std::vector<std::size_t>& use_scope = scope_paths[use_index];
            if (candidate_scope.size() > use_scope.size()) {
                return false;
            }
            return std::equal(candidate_scope.begin(),
                              candidate_scope.end(),
                              use_scope.begin(),
                              use_scope.begin() + candidate_scope.size());
        };

    const auto is_identifier_reassigned_between =
        [&](const std::vector<std::vector<std::size_t>>& scope_paths,
            std::size_t begin,
            std::size_t end,
            std::string_view identifier) {
            for (std::size_t probe = begin; probe < end && probe < lines->size(); ++probe) {
                if (!is_scope_visible_at_use(scope_paths, probe, end)) {
                    continue;
                }
                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], identifier) ||
                    IsAssignmentToIdentifier((*lines)[probe], identifier)) {
                    return true;
                }
            }
            return false;
        };

    const auto function_block_for_line =
        [&](std::size_t line_index,
            std::size_t* function_begin_out,
            std::size_t* function_end_out) -> bool {
            if (function_begin_out == nullptr || function_end_out == nullptr ||
                line_index >= lines->size()) {
                return false;
            }

            const std::size_t function_begin =
                FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
            if (function_begin >= lines->size()) {
                return false;
            }

            const std::size_t function_end =
                FindRenderedFunctionBlockEnd(*lines, function_begin);
            if (function_end >= lines->size() || function_end < function_begin) {
                return false;
            }

            *function_begin_out = function_begin;
            *function_end_out = function_end;
            return true;
        };

    const auto find_nearest_receiver = [&](const std::vector<std::vector<std::size_t>>& scope_paths,
                                           std::size_t line_index) -> std::string {
        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        const std::size_t lower_bound =
            function_begin == lines->size() ? 0 : function_begin + 1;
        std::string best_identifier;
        bool best_is_temp = true;
        std::size_t best_distance = lines->size() + 1;
        std::size_t scanned = 0;
        for (std::size_t probe = line_index; probe > lower_bound && scanned < 1024;) {
            --probe;
            ++scanned;
            if (!is_scope_visible_at_use(scope_paths, probe, line_index)) {
                continue;
            }

            std::string lhs;
            std::string rhs;
            if (!try_parse_assignment_candidate((*lines)[probe], &lhs, &rhs)) {
                continue;
            }
            if (is_identifier_reassigned_between(scope_paths, probe + 1, line_index, lhs)) {
                continue;
            }

            const bool lhs_is_temp = IsRenderTemporaryName(lhs);
            const std::size_t distance = line_index - probe;
            if (best_identifier.empty() ||
                (best_is_temp && !lhs_is_temp) ||
                (best_is_temp == lhs_is_temp && distance < best_distance)) {
                best_identifier = lhs;
                best_is_temp = lhs_is_temp;
                best_distance = distance;
                if (!lhs_is_temp && distance <= 16) {
                    break;
                }
            }
        }
        if (best_is_temp) {
            return {};
        }
        return best_identifier;
    };

    const auto find_function_persistent_receiver =
        [&](std::size_t line_index) -> std::string {
            std::size_t function_begin = 0;
            std::size_t function_end = 0;
            if (!function_block_for_line(line_index, &function_begin, &function_end)) {
                return {};
            }

            const auto line_mentions_identifier = [&](std::size_t probe,
                                                      std::string_view identifier) {
                if (probe >= lines->size() || probe == line_index) {
                    return false;
                }
                if (FindEnclosingRenderedFunctionBlockBegin(*lines, probe) != function_begin) {
                    return false;
                }
                return CountIdentifierUses(StripLuaLineComment((*lines)[probe]), identifier) != 0;
            };

            std::unordered_map<std::string, std::size_t> latest_assignment_line;
            std::vector<std::string> candidates;
            std::unordered_set<std::string> seen_candidates;
            for (std::size_t probe = function_begin + 1; probe < line_index; ++probe) {
                if (FindEnclosingRenderedFunctionBlockBegin(*lines, probe) != function_begin) {
                    continue;
                }

                std::string candidate;
                if (try_parse_simple_local_name((*lines)[probe], &candidate) &&
                    seen_candidates.insert(candidate).second) {
                    candidates.push_back(candidate);
                }

                std::string lhs;
                std::string rhs;
                if (!try_parse_assignment_candidate((*lines)[probe], &lhs, &rhs)) {
                    continue;
                }
                latest_assignment_line[lhs] = probe;
                if (seen_candidates.insert(lhs).second) {
                    candidates.push_back(lhs);
                }
            }

            struct CandidateScore {
                bool has_forward_mention = false;
                std::size_t nearest_forward_mention = std::numeric_limits<std::size_t>::max();
                std::size_t forward_mentions = 0;
                bool has_backward_mention = false;
                std::size_t nearest_backward_mention = std::numeric_limits<std::size_t>::max();
                std::size_t backward_mentions = 0;
                std::size_t assignment_distance = std::numeric_limits<std::size_t>::max();
            };

            const std::size_t mention_begin =
                line_index > 256 ? line_index - 256 : function_begin + 1;
            const std::size_t mention_end =
                std::min(function_end + 1, line_index + 257);

            auto better_score = [](const CandidateScore& lhs,
                                   const CandidateScore& rhs) {
                if (lhs.has_forward_mention != rhs.has_forward_mention) {
                    return lhs.has_forward_mention;
                }
                if (lhs.has_forward_mention &&
                    lhs.nearest_forward_mention != rhs.nearest_forward_mention) {
                    return lhs.nearest_forward_mention < rhs.nearest_forward_mention;
                }
                if (lhs.forward_mentions != rhs.forward_mentions) {
                    return lhs.forward_mentions > rhs.forward_mentions;
                }
                if (lhs.assignment_distance != rhs.assignment_distance) {
                    return lhs.assignment_distance < rhs.assignment_distance;
                }
                if (lhs.has_backward_mention != rhs.has_backward_mention) {
                    return lhs.has_backward_mention;
                }
                if (lhs.has_backward_mention &&
                    lhs.nearest_backward_mention != rhs.nearest_backward_mention) {
                    return lhs.nearest_backward_mention < rhs.nearest_backward_mention;
                }
                if (lhs.backward_mentions != rhs.backward_mentions) {
                    return lhs.backward_mentions > rhs.backward_mentions;
                }
                return false;
            };

            std::string best_identifier;
            CandidateScore best_score;
            bool have_best = false;
            for (const std::string& candidate : candidates) {
                const auto assignment_it = latest_assignment_line.find(candidate);
                if (assignment_it == latest_assignment_line.end()) {
                    continue;
                }

                CandidateScore score;
                score.assignment_distance = line_index - assignment_it->second;
                for (std::size_t probe = mention_begin; probe < mention_end; ++probe) {
                    if (!line_mentions_identifier(probe, candidate)) {
                        continue;
                    }

                    const std::size_t distance =
                        probe > line_index ? probe - line_index : line_index - probe;
                    if (probe > line_index) {
                        score.has_forward_mention = true;
                        score.nearest_forward_mention =
                            std::min(score.nearest_forward_mention, distance);
                        ++score.forward_mentions;
                    } else {
                        score.has_backward_mention = true;
                        score.nearest_backward_mention =
                            std::min(score.nearest_backward_mention, distance);
                        ++score.backward_mentions;
                    }
                }

                if (!have_best || better_score(score, best_score)) {
                    best_identifier = candidate;
                    best_score = score;
                    have_best = true;
                }
            }

            return best_identifier;
        };

    const auto try_rewrite_line = [](std::string_view line,
                                     std::string_view replacement,
                                     std::string* rewritten_out) -> bool {
        if (rewritten_out == nullptr || replacement.empty()) {
            return false;
        }

        const std::string_view stripped = StripLuaLineComment(line);
        const std::string_view comment =
            stripped.size() < line.size() ? std::string_view(line).substr(stripped.size())
                                          : std::string_view();

        std::string rewritten;
        rewritten.reserve(line.size() + replacement.size());
        bool changed = false;
        for (std::size_t index = 0; index < stripped.size();) {
            if (stripped[index] == '"' && index + 1 < stripped.size() && stripped[index + 1] == '"') {
                std::size_t previous_non_space = index;
                while (previous_non_space > 0 &&
                       std::isspace(static_cast<unsigned char>(stripped[previous_non_space - 1])) != 0) {
                    --previous_non_space;
                }

                std::size_t next_non_space = index + 2;
                while (next_non_space < stripped.size() &&
                       std::isspace(static_cast<unsigned char>(stripped[next_non_space])) != 0) {
                    ++next_non_space;
                }

                const bool is_method_receiver =
                    next_non_space < stripped.size() && stripped[next_non_space] == ':';
                const bool is_middle_argument =
                    previous_non_space > 0 && stripped[previous_non_space - 1] == ',' &&
                    next_non_space < stripped.size() && stripped[next_non_space] == ',';
                if (is_method_receiver || is_middle_argument) {
                    rewritten.append(replacement);
                    index += 2;
                    changed = true;
                    continue;
                }
            }

            rewritten.push_back(stripped[index]);
            ++index;
        }

        if (!changed) {
            return false;
        }

        *rewritten_out = rewritten + std::string(comment);
        return true;
    };

    const auto parse_call_line_view = [](std::string_view line,
                                         CallLineView* view_out) -> bool {
        if (view_out == nullptr) {
            return false;
        }

        const std::string_view stripped = StripLuaLineComment(line);
        const std::string comment =
            stripped.size() < line.size() ? std::string(line.substr(stripped.size()))
                                          : std::string();
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (trimmed.empty()) {
            return false;
        }

        auto parse_with_expression = [&](std::string_view expression,
                                         std::string_view full_prefix,
                                         std::string_view full_suffix) -> bool {
            std::string callee;
            std::vector<std::string> arguments;
            if (!ParseRenderedSingleCallLine(expression, &callee, &arguments)) {
                return false;
            }
            view_out->prefix = std::string(full_prefix);
            view_out->suffix = std::string(full_suffix) + comment;
            view_out->callee = std::move(callee);
            view_out->arguments = std::move(arguments);
            return true;
        };

        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (ParseRenderedAssignmentLine(trimmed, &is_local, &lhs_names, &rhs_expression) &&
            ParseRenderedSingleCallLine(rhs_expression, &view_out->callee, &view_out->arguments)) {
            const std::size_t rhs_offset = stripped.find(rhs_expression);
            if (rhs_offset == std::string_view::npos) {
                return false;
            }
            view_out->prefix = std::string(stripped.substr(0, rhs_offset));
            view_out->suffix =
                std::string(stripped.substr(rhs_offset + rhs_expression.size())) + comment;
            return true;
        }

        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            const std::string_view expression =
                TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
            const std::size_t expression_offset = stripped.find(expression);
            if (expression_offset == std::string_view::npos) {
                return false;
            }
            return parse_with_expression(expression,
                                         stripped.substr(0, expression_offset),
                                         stripped.substr(expression_offset + expression.size()));
        }

        if (trimmed.starts_with("while ") && trimmed.ends_with(" do")) {
            const std::string_view expression =
                TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9));
            const std::size_t expression_offset = stripped.find(expression);
            if (expression_offset == std::string_view::npos) {
                return false;
            }
            return parse_with_expression(expression,
                                         stripped.substr(0, expression_offset),
                                         stripped.substr(expression_offset + expression.size()));
        }

        return parse_with_expression(trimmed,
                                     stripped.substr(0, stripped.find(trimmed)),
                                     stripped.substr(stripped.find(trimmed) + trimmed.size()));
    };

    const auto render_call_line_view = [](const CallLineView& view) {
        std::string rendered = view.prefix + view.callee + "(";
        for (std::size_t index = 0; index < view.arguments.size(); ++index) {
            if (index != 0) {
                rendered += ", ";
            }
            rendered += view.arguments[index];
        }
        rendered += ")";
        rendered += view.suffix;
        return rendered;
    };

    bool changed = false;
    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (line_index >= scope_paths.size() ||
            (*lines)[line_index].find("\"\":") == std::string::npos) {
            continue;
        }

        std::string replacement = find_nearest_receiver(scope_paths, line_index);
        if (replacement.empty()) {
            replacement = find_function_persistent_receiver(line_index);
        }
        if (replacement.empty()) {
            continue;
        }

        std::string rewritten;
        if (!try_rewrite_line((*lines)[line_index], replacement, &rewritten) ||
            rewritten == (*lines)[line_index]) {
            continue;
        }
        (*lines)[line_index] = std::move(rewritten);
        changed = true;
    }

    for (std::size_t line_index = 1; line_index < lines->size(); ++line_index) {
        if ((*lines)[line_index].find("\"\"") == std::string::npos) {
            continue;
        }

        CallLineView previous;
        CallLineView current;
        if (!parse_call_line_view((*lines)[line_index - 1], &previous) ||
            !parse_call_line_view((*lines)[line_index], &current) ||
            previous.callee != current.callee ||
            previous.arguments.size() != current.arguments.size()) {
            continue;
        }

        bool compatible = true;
        bool line_changed = false;
        for (std::size_t arg_index = 0; arg_index < current.arguments.size(); ++arg_index) {
            const std::string current_argument =
                StripRenderedWholeParentheses(TrimLuaWhitespace(current.arguments[arg_index]), nullptr);
            const std::string previous_argument =
                StripRenderedWholeParentheses(TrimLuaWhitespace(previous.arguments[arg_index]), nullptr);
            if (current_argument.find("\"\"") != std::string::npos &&
                previous_argument.find("\"\"") == std::string::npos &&
                current_argument != previous_argument) {
                current.arguments[arg_index] = previous.arguments[arg_index];
                line_changed = true;
                continue;
            }
            if (current_argument != previous_argument) {
                compatible = false;
                break;
            }
        }

        if (!compatible || !line_changed) {
            continue;
        }

        const std::string rebuilt = render_call_line_view(current);
        if (rebuilt == (*lines)[line_index]) {
            continue;
        }

        (*lines)[line_index] = rebuilt;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaRedundantColonSelfArguments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_callee_start = [](std::string_view text, std::size_t open_paren) {
        int paren_depth = 0;
        int bracket_depth = 0;
        for (std::size_t pos = open_paren; pos > 0;) {
            --pos;
            const char ch = text[pos];
            if (ch == ')') {
                ++paren_depth;
                continue;
            }
            if (ch == ']') {
                ++bracket_depth;
                continue;
            }
            if (ch == '(') {
                if (paren_depth > 0) {
                    --paren_depth;
                    continue;
                }
                return pos + 1;
            }
            if (ch == '[') {
                if (bracket_depth > 0) {
                    --bracket_depth;
                    continue;
                }
                return pos + 1;
            }
            if (paren_depth > 0 || bracket_depth > 0) {
                continue;
            }

            switch (ch) {
            case ' ':
            case '\t':
            case ',':
            case ';':
            case '+':
            case '-':
            case '*':
            case '/':
            case '%':
            case '^':
            case '#':
            case '=':
            case '<':
            case '>':
            case '~':
            case '&':
            case '|':
            case '{':
            case '}':
                return pos + 1;
            default:
                break;
            }
        }
        return std::size_t{0};
    };

    const auto preceded_by_function_keyword = [](std::string_view text,
                                                 std::size_t expression_start) {
        std::string_view prefix = text.substr(0, expression_start);
        prefix = TrimLuaWhitespace(prefix);
        if (prefix.empty()) {
            return false;
        }

        const std::size_t last_space = prefix.find_last_of(" \t");
        const std::string_view token =
            last_space == std::string_view::npos ? prefix : prefix.substr(last_space + 1);
        return token == "function";
    };

    const auto try_rewrite_line = [&](std::string_view line,
                                      std::string* rewritten_out) -> bool {
        if (rewritten_out == nullptr) {
            return false;
        }

        const std::string_view stripped = StripLuaLineComment(line);
        if (TrimLuaWhitespace(stripped).empty()) {
            return false;
        }

        std::string rewritten_code(stripped);
        bool line_changed = false;
        bool in_single_quote = false;
        bool in_double_quote = false;
        for (std::size_t index = 0; index < rewritten_code.size(); ++index) {
            const char ch = rewritten_code[index];
            if (in_single_quote) {
                if (ch == '\\' && index + 1 < rewritten_code.size()) {
                    ++index;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }
            if (in_double_quote) {
                if (ch == '\\' && index + 1 < rewritten_code.size()) {
                    ++index;
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
            if (ch != '(') {
                continue;
            }

            std::size_t close_paren = std::string_view::npos;
            if (!FindRenderedMatchingParen(rewritten_code, index, &close_paren)) {
                continue;
            }

            const std::size_t callee_start = find_callee_start(rewritten_code, index);
            if (callee_start >= index ||
                preceded_by_function_keyword(rewritten_code, callee_start)) {
                continue;
            }

            const std::string_view callee_view = TrimLuaWhitespace(
                std::string_view(rewritten_code).substr(callee_start, index - callee_start));
            if (callee_view.empty()) {
                continue;
            }

            const std::string_view arguments_view =
                TrimLuaWhitespace(std::string_view(rewritten_code)
                                      .substr(index + 1, close_paren - index - 1));
            const std::string call_expression =
                std::string(callee_view) + "(" + std::string(arguments_view) + ")";
            std::string parsed_callee;
            std::vector<std::string> arguments;
            if (arguments_view.empty() ||
                !ParseRenderedSingleCallLine(call_expression, &parsed_callee, &arguments) ||
                arguments.empty()) {
                continue;
            }

            std::string rewritten_call;
            if (!TryCanonicalizeLuaCallExpressionWithIdentity(parsed_callee,
                                                              arguments,
                                                              &rewritten_call)) {
                continue;
            }

            rewritten_code.replace(callee_start,
                                   close_paren - callee_start + 1,
                                   rewritten_call);
            index = callee_start + rewritten_call.size() - 1;
            line_changed = true;
        }

        if (!line_changed) {
            return false;
        }

        const std::string_view comment =
            stripped.size() < line.size() ? std::string_view(line).substr(stripped.size())
                                          : std::string_view();
        *rewritten_out = rewritten_code + std::string(comment);
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string rewritten;
        if (!try_rewrite_line((*lines)[line_index], &rewritten) ||
            rewritten == (*lines)[line_index]) {
            continue;
        }
        (*lines)[line_index] = std::move(rewritten);
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaIntegralFloatLiterals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view stripped = StripLuaLineComment(line);
        const std::string_view comment =
            stripped.size() < line.size() ? std::string_view(line).substr(stripped.size())
                                          : std::string_view();

        std::string rewritten;
        rewritten.reserve(stripped.size());
        bool line_changed = false;
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        for (std::size_t index = 0; index < stripped.size();) {
            const char ch = stripped[index];
            if (escaped) {
                rewritten.push_back(ch);
                escaped = false;
                ++index;
                continue;
            }
            if ((in_single_quote || in_double_quote) && ch == '\\') {
                rewritten.push_back(ch);
                escaped = true;
                ++index;
                continue;
            }
            if (!in_double_quote && ch == '\'') {
                rewritten.push_back(ch);
                in_single_quote = !in_single_quote;
                ++index;
                continue;
            }
            if (!in_single_quote && ch == '"') {
                rewritten.push_back(ch);
                in_double_quote = !in_double_quote;
                ++index;
                continue;
            }
            if (in_single_quote || in_double_quote ||
                !std::isdigit(static_cast<unsigned char>(ch))) {
                rewritten.push_back(ch);
                ++index;
                continue;
            }

            const std::size_t integer_begin = index;
            std::size_t integer_end = index;
            while (integer_end < stripped.size() &&
                   std::isdigit(static_cast<unsigned char>(stripped[integer_end]))) {
                ++integer_end;
            }
            if (integer_end + 1 >= stripped.size() || stripped[integer_end] != '.' ||
                !std::isdigit(static_cast<unsigned char>(stripped[integer_end + 1]))) {
                rewritten.append(stripped.substr(integer_begin, integer_end - integer_begin));
                index = integer_end;
                continue;
            }

            std::size_t fractional_end = integer_end + 1;
            bool all_zero_fraction = true;
            while (fractional_end < stripped.size() &&
                   std::isdigit(static_cast<unsigned char>(stripped[fractional_end]))) {
                if (stripped[fractional_end] != '0') {
                    all_zero_fraction = false;
                }
                ++fractional_end;
            }

            const bool has_exponent =
                fractional_end < stripped.size() &&
                (stripped[fractional_end] == 'e' || stripped[fractional_end] == 'E');
            if (!all_zero_fraction || has_exponent) {
                rewritten.append(stripped.substr(integer_begin,
                                                 fractional_end - integer_begin));
                index = fractional_end;
                continue;
            }

            rewritten.append(stripped.substr(integer_begin, integer_end - integer_begin));
            index = fractional_end;
            line_changed = true;
        }

        if (!line_changed) {
            continue;
        }

        line = rewritten + std::string(comment);
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaPendingRegisterMethodResultCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_pending_register_banner = [](std::string_view line) {
        return TrimLuaWhitespace(line) == "-- DECOMPILER ERROR: Overwrote pending register.";
    };

    const auto parse_zero_arg_method_result_expression =
        [](std::string_view expression,
           std::string* receiver_out,
           std::string* method_out,
           std::string* call_expression_out) -> bool {
        const std::string normalized_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        if (normalized_expression.size() < 4) {
            return false;
        }

        const std::size_t open_paren = normalized_expression.find("()");
        if (open_paren == std::string::npos) {
            return false;
        }

        const std::string_view method_expression =
            TrimLuaWhitespace(std::string_view(normalized_expression).substr(0, open_paren));
        const std::size_t separator = method_expression.find_last_of(".:");
        if (separator == std::string_view::npos || separator == 0 ||
            separator + 1 >= method_expression.size()) {
            return false;
        }

        const std::string_view receiver =
            TrimLuaWhitespace(method_expression.substr(0, separator));
        const std::string_view method = method_expression.substr(separator + 1);
        if (!IsRenderedSafePrefixExpression(receiver) || !IsValidLuaIdentifier(method)) {
            return false;
        }

        if (receiver_out != nullptr) {
            *receiver_out = std::string(receiver);
        }
        if (method_out != nullptr) {
            *method_out = std::string(method);
        }
        if (call_expression_out != nullptr) {
            *call_expression_out = normalized_expression.substr(0, open_paren + 2);
        }
        return true;
    };

    const auto parse_temp_field_target = [](std::string_view expression,
                                            std::string* temp_out) -> bool {
        const std::string normalized_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        const std::size_t dot = normalized_expression.find('.');
        if (dot == std::string::npos || dot == 0) {
            return false;
        }

        const std::string_view temp_name =
            std::string_view(normalized_expression).substr(0, dot);
        const std::string_view field_name =
            std::string_view(normalized_expression).substr(dot + 1);
        if (!IsRenderTemporaryName(temp_name) || !IsValidLuaIdentifier(field_name)) {
            return false;
        }

        if (temp_out != nullptr) {
            *temp_out = std::string(temp_name);
        }
        return true;
    };

    const auto parse_zero_arg_method_reference =
        [](std::string_view expression,
           std::string* receiver_out,
           std::string* method_out) -> bool {
        const std::string normalized_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        if (normalized_expression.find('(') != std::string::npos ||
            normalized_expression.find(')') != std::string::npos) {
            return false;
        }

        const std::size_t separator = normalized_expression.find_last_of(".:");
        if (separator == std::string_view::npos || separator == 0 ||
            separator + 1 >= normalized_expression.size()) {
            return false;
        }

        const std::string_view receiver =
            TrimLuaWhitespace(std::string_view(normalized_expression).substr(0, separator));
        const std::string_view method =
            std::string_view(normalized_expression).substr(separator + 1);
        if (!IsRenderedSafePrefixExpression(receiver) || !IsValidLuaIdentifier(method)) {
            return false;
        }

        if (receiver_out != nullptr) {
            *receiver_out = std::string(receiver);
        }
        if (method_out != nullptr) {
            *method_out = std::string(method);
        }
        return true;
    };

    const auto has_prior_assignment_to_temp =
        [&](const std::vector<std::vector<std::size_t>>& scope_paths,
            std::size_t line_index,
            std::string_view temp_name) {
            for (std::size_t probe = line_index; probe > 0;) {
                --probe;
                if (probe >= scope_paths.size() || line_index >= scope_paths.size() ||
                    scope_paths[probe] != scope_paths[line_index]) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                       &probe_is_local,
                                                       &probe_lhs,
                                                       &probe_rhs)) {
                    continue;
                }

                const std::string normalized_lhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr);
                if (normalized_lhs == temp_name) {
                    return true;
                }
            }
            return false;
        };

    const auto try_parse_assignment_like_line =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            const auto starts_with = [](std::string_view text, std::string_view prefix) {
                return text.size() >= prefix.size() && text.substr(0, prefix.size()) == prefix;
            };
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }

            if (ParseRenderedSingleAssignmentLine(line, is_local_out, lhs_out, rhs_out)) {
                return true;
            }

            const std::string_view stripped = StripLuaLineComment(line);
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.empty() || starts_with(trimmed, "if ") ||
                starts_with(trimmed, "elseif ") || starts_with(trimmed, "while ") ||
                starts_with(trimmed, "for ") || starts_with(trimmed, "return ") ||
                trimmed == "return") {
                return false;
            }

            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                return false;
            }

            std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
            std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
            bool is_local = false;
            if (starts_with(lhs, "local ")) {
                is_local = true;
                lhs = TrimLuaWhitespace(lhs.substr(6));
            }
            if (lhs.empty() || rhs.empty()) {
                return false;
            }

            *is_local_out = is_local;
            *lhs_out = std::string(lhs);
            *rhs_out = std::string(rhs);
            return true;
        };

    const auto replace_function_placeholder_uses =
        [](std::string_view line,
           std::string_view replacement,
           std::string* rewritten_out) -> bool {
            if (rewritten_out == nullptr) {
                return false;
            }

            const std::string_view stripped = StripLuaLineComment(line);
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.empty() || trimmed.starts_with("function(") ||
                trimmed.starts_with("function ") ||
                trimmed.find("= function(") != std::string_view::npos) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            bool changed = false;
            std::string rewritten;
            rewritten.reserve(line.size() + replacement.size());
            for (std::size_t index = 0; index < line.size();) {
                const char ch = line[index];
                if (escaped) {
                    rewritten.push_back(ch);
                    escaped = false;
                    ++index;
                    continue;
                }
                if ((in_single_quote || in_double_quote) && ch == '\\') {
                    rewritten.push_back(ch);
                    escaped = true;
                    ++index;
                    continue;
                }
                if (!in_double_quote && ch == '\'') {
                    rewritten.push_back(ch);
                    in_single_quote = !in_single_quote;
                    ++index;
                    continue;
                }
                if (!in_single_quote && ch == '"') {
                    rewritten.push_back(ch);
                    in_double_quote = !in_double_quote;
                    ++index;
                    continue;
                }
                if (!in_single_quote && !in_double_quote &&
                    line.compare(index, 10, "function()") == 0) {
                    rewritten.append(replacement);
                    index += 10;
                    changed = true;
                    continue;
                }

                rewritten.push_back(ch);
                ++index;
            }

            if (!changed) {
                return false;
            }

            *rewritten_out = std::move(rewritten);
            return true;
        };

    bool changed = false;
    RunRenderedLuaCleanupPassLoop(
        "SimplifyRenderedLuaPendingRegisterMethodResultCarries",
        16,
        [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size()) {
                continue;
            }

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!try_parse_assignment_like_line((*lines)[line_index],
                                                &is_local,
                                                &lhs_expression,
                                                &rhs_expression)) {
                continue;
            }

            const std::string normalized_lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            std::string builder_receiver;
            std::string builder_method;
            std::string builder_call_expression;
            if (!parse_zero_arg_method_result_expression(normalized_lhs,
                                                         &builder_receiver,
                                                         &builder_method,
                                                         &builder_call_expression)) {
                continue;
            }
            if (normalized_lhs.size() <= builder_call_expression.size() + 1 ||
                normalized_lhs.compare(0,
                                       builder_call_expression.size(),
                                       builder_call_expression) != 0 ||
                normalized_lhs[builder_call_expression.size()] != '.') {
                continue;
            }
            const std::string builder_field_suffix =
                std::string(TrimLuaWhitespace(
                    std::string_view(normalized_lhs).substr(builder_call_expression.size() + 1)));
            if (builder_field_suffix.empty()) {
                continue;
            }

            std::size_t nearby_use_count = 1;
            std::size_t function_placeholder_use_count = 0;
            std::size_t banner_index = lines->size();
            std::string builder_temp_name;
            const std::size_t scan_limit = std::min(lines->size(), line_index + 12);
            for (std::size_t probe = line_index + 1; probe < scan_limit; ++probe) {
                if (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index]) {
                    break;
                }

                if (is_pending_register_banner((*lines)[probe])) {
                    banner_index = probe;
                    continue;
                }

                if (CountIdentifierUses((*lines)[probe], builder_call_expression) != 0) {
                    ++nearby_use_count;
                    continue;
                }
                if ((*lines)[probe].find("function()") != std::string::npos) {
                    ++function_placeholder_use_count;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (!try_parse_assignment_like_line((*lines)[probe],
                                                    &probe_is_local,
                                                    &probe_lhs,
                                                    &probe_rhs)) {
                    continue;
                }

                if (!builder_temp_name.empty()) {
                    continue;
                }

                std::string candidate_temp_name;
                if (!parse_temp_field_target(probe_lhs, &candidate_temp_name) ||
                    has_prior_assignment_to_temp(scope_paths, probe, candidate_temp_name)) {
                    continue;
                }
                builder_temp_name = std::move(candidate_temp_name);
            }

            if (builder_temp_name.empty()) {
                builder_temp_name =
                    "__refii_" + builder_method + "_" + std::to_string(line_index + 1);
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          indent + "local " + builder_temp_name + " = " + builder_call_expression);
            ++banner_index;
            ++line_index;

            // `lines` changed above; refresh scope paths before bounded rewrites so
            // scope membership checks stay aligned with current indexes.
            std::vector<std::vector<std::size_t>> rewrite_scope_paths;
            ComputeRenderedScopePaths(*lines, &rewrite_scope_paths);
            if (line_index >= rewrite_scope_paths.size()) {
                changed = true;
                pass_changed = true;
                break;
            }
            const std::vector<std::size_t> rewrite_scope = rewrite_scope_paths[line_index];

            const std::size_t rewrite_limit =
                std::min(lines->size(),
                         (banner_index < lines->size() ? banner_index + 8 : line_index + 12));
            for (std::size_t probe = line_index; probe < rewrite_limit; ++probe) {
                if (probe >= rewrite_scope_paths.size() ||
                    rewrite_scope_paths[probe] != rewrite_scope) {
                    break;
                }
                if (is_pending_register_banner((*lines)[probe])) {
                    continue;
                }

                std::string rewritten_line;
                if (ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine(
                        (*lines)[probe],
                        builder_call_expression,
                        builder_temp_name,
                        &rewritten_line) &&
                    !rewritten_line.empty()) {
                    (*lines)[probe] = std::move(rewritten_line);
                }
                if (replace_function_placeholder_uses((*lines)[probe],
                                                      builder_temp_name,
                                                      &rewritten_line) &&
                    !rewritten_line.empty()) {
                    (*lines)[probe] = std::move(rewritten_line);
                }
            }

            if (banner_index < lines->size() && is_pending_register_banner((*lines)[banner_index])) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(banner_index));
            }
            if (nearby_use_count < 2 && function_placeholder_use_count == 0 &&
                banner_index >= lines->size()) {
                (*lines)[line_index] =
                    indent + builder_temp_name + "." + builder_field_suffix + " = " + rhs_expression;
            }
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
                if (line_index >= scope_paths.size()) {
                    continue;
                }

                bool is_local = false;
                std::string lhs_expression;
                std::string rhs_expression;
                if (!try_parse_assignment_like_line((*lines)[line_index],
                                                    &is_local,
                                                    &lhs_expression,
                                                    &rhs_expression)) {
                    continue;
                }

            const std::string temp_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            if (!IsRenderTemporaryName(temp_name)) {
                continue;
            }

            std::string receiver;
            std::string method;
            if (!parse_zero_arg_method_reference(rhs_expression, &receiver, &method)) {
                continue;
            }

            std::size_t temp_use_count = 0;
            bool saw_pending_register_banner = false;
            std::size_t pending_register_banner_index = lines->size();
            const std::size_t scan_limit = std::min(lines->size(), line_index + 8);
            for (std::size_t probe = line_index + 1; probe < scan_limit; ++probe) {
                if (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index]) {
                    break;
                }

                if (is_pending_register_banner((*lines)[probe])) {
                    saw_pending_register_banner = true;
                    pending_register_banner_index = probe;
                    continue;
                }

                temp_use_count += CountIdentifierUses((*lines)[probe], temp_name);
            }

            bool saw_prior_pending_register_banner = false;
            std::size_t prior_pending_register_banner_index = lines->size();
            bool temp_used_before_banner = false;
            const std::size_t reverse_limit = line_index > 8 ? line_index - 8 : 0;
            for (std::size_t probe = line_index; probe > reverse_limit;) {
                --probe;
                if (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index]) {
                    continue;
                }

                if (is_pending_register_banner((*lines)[probe])) {
                    saw_prior_pending_register_banner = true;
                    prior_pending_register_banner_index = probe;
                    continue;
                }

                if (!saw_prior_pending_register_banner) {
                    continue;
                }

                if (CountIdentifierUses((*lines)[probe], temp_name) != 0) {
                    temp_used_before_banner = true;
                    break;
                }
            }

                if ((!saw_pending_register_banner && !saw_prior_pending_register_banner) ||
                    temp_use_count < 2) {
                    continue;
                }

                const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
                if (saw_prior_pending_register_banner && temp_used_before_banner) {
                    const std::string fresh_temp_name =
                        "__refii_" + method + "_" + std::to_string(line_index + 1);
                    (*lines)[line_index] = indent + "local " + fresh_temp_name + " = " +
                                           receiver + ":" + method + "()";

                const std::size_t rewrite_limit = std::min(lines->size(), line_index + 12);
                for (std::size_t probe = line_index + 1; probe < rewrite_limit; ++probe) {
                    if (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index]) {
                        break;
                    }
                    if (is_pending_register_banner((*lines)[probe])) {
                        continue;
                    }

                    std::string rewritten_line;
                    if (ReplaceRenderedIdentifierUses((*lines)[probe],
                                                      temp_name,
                                                      fresh_temp_name,
                                                      &rewritten_line) &&
                        !rewritten_line.empty()) {
                        (*lines)[probe] = std::move(rewritten_line);
                    }
                }

                if (prior_pending_register_banner_index < lines->size() &&
                    is_pending_register_banner((*lines)[prior_pending_register_banner_index])) {
                    lines->erase(lines->begin() +
                                 static_cast<std::ptrdiff_t>(prior_pending_register_banner_index));
                }
                } else {
                    (*lines)[line_index] = indent +
                                           (ShouldEmitLocalForRenderedIdentifierTarget(*lines,
                                                                                        line_index,
                                                                                        temp_name)
                                                ? "local "
                                                : std::string()) +
                                           temp_name + " = " + receiver + ":" + method + "()";
                }
                if (pending_register_banner_index < lines->size() &&
                    is_pending_register_banner((*lines)[pending_register_banner_index])) {
                    lines->erase(lines->begin() +
                                 static_cast<std::ptrdiff_t>(pending_register_banner_index));
                }
                changed = true;
                pass_changed = true;
                break;
            }
        }
        return pass_changed;
    });

    return changed;
}
