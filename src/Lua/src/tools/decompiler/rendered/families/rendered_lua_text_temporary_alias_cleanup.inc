bool SimplifyRenderedLuaTopLevelNamespaceTableAliasChains(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
            continue;
        }

        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !seed_is_local || !IsRenderedSafePrefixExpression(seed_rhs)) {
            continue;
        }

        std::size_t export_line = lines->size();
        for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
            if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                break;
            }
            if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                export_line = probe;
                break;
            }
        }
        if (export_line >= lines->size()) {
            continue;
        }

        bool export_is_local = false;
        std::string export_lhs;
        std::string export_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[export_line],
                                               &export_is_local,
                                               &export_lhs,
                                               &export_rhs) ||
            export_is_local || TrimLuaWhitespace(export_rhs) != "{}" ||
            !export_lhs.starts_with(seed_lhs + ".")) {
            continue;
        }

        const std::string_view export_suffix =
            TrimLuaWhitespace(std::string_view(export_lhs).substr(seed_lhs.size() + 1));
        if (export_suffix.empty() || export_suffix.find('.') != std::string_view::npos ||
            export_suffix.find(':') != std::string_view::npos ||
            export_suffix.find('[') != std::string_view::npos ||
            !IsValidLuaIdentifier(export_suffix)) {
            continue;
        }

        const std::string exported_name(export_suffix);
        const std::string replacement_export =
            std::string(TrimLuaWhitespace(seed_rhs)) + "." + exported_name;
        const std::string export_indent(std::string(GetRenderedLuaIndent((*lines)[export_line])));
        (*lines)[export_line] = export_indent + replacement_export + " = {}";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        ComputeRenderedScopePaths(*lines, &scope_paths);
        changed = true;

        std::size_t probe = line_index;
        while (probe < lines->size()) {
            if (probe >= scope_paths.size()) {
                break;
            }
            if (!scope_paths[probe].empty()) {
                ++probe;
                continue;
            }

            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                 &probe_is_local,
                                                 &probe_lhs,
                                                 &probe_rhs) &&
                probe_lhs == seed_lhs) {
                const std::string normalized_probe_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_rhs), nullptr);
                if (normalized_probe_rhs == exported_name ||
                    normalized_probe_rhs == replacement_export) {
                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(probe));
                    ComputeRenderedScopePaths(*lines, &scope_paths);
                    changed = true;
                    continue;
                }
                break;
            }

            if (CountIdentifierUses((*lines)[probe], seed_lhs) == 0) {
                ++probe;
                continue;
            }

            std::string rewritten_line;
            if (!ReplaceRenderedIdentifierUses((*lines)[probe],
                                              seed_lhs,
                                              exported_name,
                                              &rewritten_line)) {
                break;
            }

            (*lines)[probe] = std::move(rewritten_line);
            changed = true;
            ++probe;
        }

        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

namespace {

bool TryRewriteRenderedBinaryMethodCarryExpression(std::string_view expression,
                                                   std::string* repaired_expression) {
    if (repaired_expression == nullptr) {
        return false;
    }

    static constexpr std::array<std::string_view, 4> kLengthMethodSuffixes = {
        ":GetLength()", ":GetLengthXY()", ":GetSquaredLength()", ":GetSquaredLengthXY()"};

    const auto find_top_level_add_sub = [](std::string_view expression,
                                           std::size_t* op_pos_out) -> bool {
        if (op_pos_out == nullptr) {
            return false;
        }

        bool in_single_quote = false;
        bool in_double_quote = false;
        int paren_depth = 0;
        int brace_depth = 0;
        int bracket_depth = 0;
        std::size_t best = std::string_view::npos;
        for (std::size_t i = 0; i + 2 < expression.size(); ++i) {
            const char ch = expression[i];
            if (in_single_quote) {
                if (ch == '\\' && i + 1 < expression.size()) {
                    ++i;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }
            if (in_double_quote) {
                if (ch == '\\' && i + 1 < expression.size()) {
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
            if (expression.substr(i, 3) == " + " || expression.substr(i, 3) == " - ") {
                best = i;
            }
        }

        if (best == std::string_view::npos) {
            return false;
        }
        *op_pos_out = best;
        return true;
    };

    const std::string normalized_expression =
        StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
    if (normalized_expression.empty()) {
        return false;
    }

    for (std::string_view suffix : kLengthMethodSuffixes) {
        std::size_t suffix_pos = normalized_expression.rfind(suffix);
        if (suffix_pos == std::string::npos) {
            continue;
        }

        const std::string_view prefix_view =
            TrimLuaWhitespace(std::string_view(normalized_expression).substr(0, suffix_pos));
        if (prefix_view.empty()) {
            continue;
        }

        std::size_t op_pos = std::string_view::npos;
        if (!find_top_level_add_sub(prefix_view, &op_pos)) {
            continue;
        }

        const std::string_view left = TrimLuaWhitespace(prefix_view.substr(0, op_pos));
        const std::string_view right = TrimLuaWhitespace(prefix_view.substr(op_pos + 3));
        if (left.empty() || right.empty() || !IsRenderedSafePrefixExpression(right) ||
            LooksLikeRenderedNumericLiteral(left)) {
            continue;
        }

        *repaired_expression = "(" + std::string(prefix_view) + ")" + std::string(suffix) +
                               normalized_expression.substr(suffix_pos + suffix.size());
        return true;
    }

    return false;
}

bool TryRewriteRenderedBinaryMethodCarryExpressionRecursive(
    std::string_view expression,
    std::string* repaired_expression,
    int depth = 0) {
    if (repaired_expression == nullptr || depth > 8) {
        return false;
    }

    if (TryRewriteRenderedBinaryMethodCarryExpression(expression, repaired_expression)) {
        return true;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    std::vector<std::size_t> open_stack;
    for (std::size_t index = 0; index < expression.size(); ++index) {
        const char ch = expression[index];
        if (in_single_quote) {
            if (ch == '\\' && index + 1 < expression.size()) {
                ++index;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && index + 1 < expression.size()) {
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
        if (ch == '(') {
            open_stack.push_back(index);
            continue;
        }
        if (ch != ')' || open_stack.empty()) {
            continue;
        }

        const std::size_t open = open_stack.back();
        open_stack.pop_back();
        if (index <= open + 1) {
            continue;
        }

        const std::string_view inner = expression.substr(open + 1, index - open - 1);
        std::string repaired_inner;
        if (!TryRewriteRenderedBinaryMethodCarryExpressionRecursive(
                inner,
                &repaired_inner,
                depth + 1)) {
            continue;
        }

        *repaired_expression = std::string(expression.substr(0, open + 1)) + repaired_inner +
                               std::string(expression.substr(index));
        return true;
    }

    return false;
}

}  // namespace

bool SimplifyRenderedLuaBinaryMethodCallCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index], &is_local, &lhs, &rhs)) {
            continue;
        }

        std::string repaired_rhs;
        if (!TryRewriteRenderedBinaryMethodCarryExpressionRecursive(rhs, &repaired_rhs)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + (is_local ? "local " : "") + lhs + " = " + repaired_rhs;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaConditionBinaryMethodCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_if_condition = [](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }
        line = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!line.starts_with("if ") || !line.ends_with(" then")) {
            return false;
        }
        *condition = std::string(TrimLuaWhitespace(line.substr(3, line.size() - 3 - 5)));
        return !condition->empty();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string condition;
        if (!parse_if_condition((*lines)[line_index], &condition)) {
            continue;
        }

        std::string repaired_condition;
        if (!TryRewriteRenderedBinaryMethodCarryExpressionRecursive(
                condition,
                &repaired_condition)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "if " + repaired_condition + " then";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateTemporaryAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool temp_is_local = false;
        std::string temp_lhs;
        std::string temp_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &temp_is_local,
                                               &temp_lhs,
                                               &temp_rhs) ||
            !IsRenderTemporaryName(temp_lhs)) {
            continue;
        }

        const std::string_view temp_trimmed = TrimLuaWhitespace(temp_rhs);
        int paren_balance = 0;
        int brace_balance = 0;
        if (temp_rhs.find('\n') != std::string::npos || temp_trimmed.empty() ||
            temp_trimmed.front() == '{' || temp_rhs.find("function(") != std::string::npos ||
            HasPositiveLuaExpressionBalance(temp_rhs, &paren_balance, &brace_balance) ||
            CountIdentifierUses(temp_rhs, temp_lhs) != 0) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_rhs != temp_lhs || assign_lhs == temp_lhs) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();

        bool used_later = false;
        for (std::size_t probe = line_index + 2; probe < probe_limit; ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], temp_lhs) ||
                IsAssignmentToIdentifier((*lines)[probe], temp_lhs)) {
                break;
            }
            if (CountIdentifierUses((*lines)[probe], temp_lhs) != 0) {
                used_later = true;
                break;
            }
        }
        if (used_later) {
            continue;
        }

        const std::size_t equal = (*lines)[line_index + 1].find('=');
        if (equal == std::string::npos) {
            continue;
        }

        (*lines)[line_index + 1] =
            (*lines)[line_index + 1].substr(0, equal + 1) + " " + temp_rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateTemporaryCallArgumentUses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto try_extract_arithmetic_base = [](std::string_view expression,
                                                std::string* base_out) {
        if (base_out == nullptr) {
            return false;
        }
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        int paren_depth = 0;
        for (std::size_t index = 0; index < normalized.size(); ++index) {
            const char ch = normalized[index];
            if (ch == '(' || ch == '{' || ch == '[') {
                ++paren_depth;
                continue;
            }
            if (ch == ')' || ch == '}' || ch == ']') {
                if (paren_depth > 0) {
                    --paren_depth;
                }
                continue;
            }
            if (paren_depth != 0 || (ch != '+' && ch != '-')) {
                continue;
            }

            const std::string left =
                StripRenderedWholeParentheses(TrimLuaWhitespace(
                                                  std::string_view(normalized).substr(0, index)),
                                              nullptr);
            const std::string right =
                StripRenderedWholeParentheses(TrimLuaWhitespace(
                                                  std::string_view(normalized).substr(index + 1)),
                                              nullptr);
            if (left.empty() || right.empty() || !IsRenderedSafePrefixExpression(left)) {
                continue;
            }
            bool numeric_right = true;
            for (char digit : right) {
                if (!std::isdigit(static_cast<unsigned char>(digit)) && digit != '.') {
                    numeric_right = false;
                    break;
                }
            }
            if (!numeric_right) {
                continue;
            }
            *base_out = left;
            return true;
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool temp_is_local = false;
        std::string temp_lhs;
        std::string temp_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &temp_is_local,
                                               &temp_lhs,
                                               &temp_rhs) ||
            !IsRenderTemporaryName(temp_lhs)) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(temp_rhs), nullptr);
        int paren_balance = 0;
        int brace_balance = 0;
        if (temp_rhs.find('\n') != std::string::npos || normalized_rhs.empty() ||
            temp_rhs.find("function(") != std::string::npos ||
            HasPositiveLuaExpressionBalance(temp_rhs, &paren_balance, &brace_balance) ||
            CountIdentifierUses(temp_rhs, temp_lhs) != 0) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine((*lines)[line_index + 1], &callee, &arguments) ||
            CountIdentifierUses(callee, temp_lhs) != 0 || arguments.empty()) {
            continue;
        }

        bool replaced_any = false;
        std::vector<std::string> rewritten_arguments = arguments;
        for (std::size_t arg_index = 0; arg_index < arguments.size(); ++arg_index) {
            const std::string normalized_argument =
                StripRenderedWholeParentheses(TrimLuaWhitespace(arguments[arg_index]), nullptr);
            if (normalized_argument != temp_lhs) {
                if (CountIdentifierUses(arguments[arg_index], temp_lhs) != 0) {
                    replaced_any = false;
                    break;
                }
                continue;
            }

            rewritten_arguments[arg_index] = temp_rhs;
            replaced_any = true;
        }
        if (!replaced_any && CountIdentifierUses((*lines)[line_index + 1], temp_lhs) == 0) {
            std::string arithmetic_base;
            if (try_extract_arithmetic_base(temp_rhs, &arithmetic_base)) {
                for (std::size_t arg_index = arguments.size(); arg_index > 0; --arg_index) {
                    const std::size_t candidate_index = arg_index - 1;
                    const std::string normalized_argument =
                        StripRenderedWholeParentheses(
                            TrimLuaWhitespace(arguments[candidate_index]), nullptr);
                    if (normalized_argument == arithmetic_base) {
                        rewritten_arguments[candidate_index] = temp_rhs;
                        replaced_any = true;
                        break;
                    }
                }
            }
        }
        if (!replaced_any) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();

        bool used_later = false;
        for (std::size_t probe = line_index + 2; probe < probe_limit; ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], temp_lhs) ||
                IsAssignmentToIdentifier((*lines)[probe], temp_lhs)) {
                break;
            }
            if (CountIdentifierUses((*lines)[probe], temp_lhs) != 0) {
                used_later = true;
                break;
            }
        }
        if (used_later) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        std::string rebuilt = indent + callee + "(";
        for (std::size_t arg_index = 0; arg_index < rewritten_arguments.size(); ++arg_index) {
            if (arg_index != 0) {
                rebuilt += ", ";
            }
            rebuilt += rewritten_arguments[arg_index];
        }
        rebuilt += ")";

        const std::string_view stripped = StripLuaLineComment((*lines)[line_index + 1]);
        const std::string_view comment =
            stripped.size() < (*lines)[line_index + 1].size()
                ? std::string_view((*lines)[line_index + 1]).substr(stripped.size())
                : std::string_view();
        (*lines)[line_index + 1] = rebuilt + std::string(comment);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateTemporaryStatementUses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto next_nonempty_index = [&](std::size_t start) -> std::size_t {
        std::size_t index = start;
        while (index < lines->size() &&
               TrimLuaWhitespace(StripLuaLineComment((*lines)[index])).empty()) {
            ++index;
        }
        return index;
    };

    return RunRenderedLuaCleanupPassLoop(__func__, 32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size()) {
                continue;
            }

            bool temp_is_local = false;
            std::string temp_lhs;
            std::string temp_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &temp_is_local,
                                                   &temp_lhs,
                                                   &temp_rhs) ||
                !temp_is_local || !IsRenderTemporaryName(temp_lhs)) {
                continue;
            }

            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(temp_rhs), nullptr);
            int paren_balance = 0;
            int brace_balance = 0;
            if (temp_rhs.find('\n') != std::string::npos || normalized_rhs.empty() ||
                temp_rhs.find("function(") != std::string::npos ||
                HasPositiveLuaExpressionBalance(temp_rhs, &paren_balance, &brace_balance) ||
                CountIdentifierUses(temp_rhs, temp_lhs) != 0) {
                continue;
            }

            const std::size_t use_index = next_nonempty_index(line_index + 1);
            if (use_index >= lines->size() || use_index >= scope_paths.size() ||
                scope_paths[use_index] != scope_paths[line_index] ||
                CountIdentifierUses((*lines)[use_index], temp_lhs) != 1) {
                continue;
            }

            bool used_later = false;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = use_index + 1; probe < probe_limit; ++probe) {
                if (probe >= scope_paths.size() ||
                    scope_paths[probe] != scope_paths[line_index]) {
                    continue;
                }
                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], temp_lhs) ||
                    IsAssignmentToIdentifier((*lines)[probe], temp_lhs)) {
                    break;
                }
                if (CountIdentifierUses((*lines)[probe], temp_lhs) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (used_later) {
                continue;
            }

            std::string rewritten;
            if (!ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine(
                    (*lines)[use_index],
                    temp_lhs,
                    WrapInlineExpression(temp_rhs),
                    &rewritten) ||
                rewritten.empty() || rewritten == (*lines)[use_index]) {
                continue;
            }

            (*lines)[use_index] = std::move(rewritten);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            return true;
        }

        return false;
    });
}

bool SimplifyRenderedLuaSwapAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        bool second_is_local = false;
        std::string second_lhs;
        std::string second_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &first_is_local,
                                               &first_lhs,
                                               &first_rhs) ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &second_is_local,
                                               &second_lhs,
                                               &second_rhs) ||
            first_is_local || second_is_local || first_lhs == second_lhs) {
            continue;
        }

        const std::string normalized_first_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_lhs), nullptr);
        const std::string normalized_first_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_rhs), nullptr);
        const std::string normalized_second_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(second_lhs), nullptr);
        const std::string normalized_second_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(second_rhs), nullptr);
        if (normalized_first_lhs != normalized_second_rhs ||
            normalized_second_lhs != normalized_first_rhs) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + normalized_first_lhs + ", " + normalized_second_lhs +
                               " = " + normalized_first_rhs + ", " + normalized_second_rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaSimpleGuardedDefaultAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto expression_uses_target = [](std::string_view expression,
                                           std::string_view target) -> bool {
        if (target.empty()) {
            return false;
        }
        if (IsValidLuaIdentifier(target)) {
            return CountIdentifierUses(expression, target) != 0;
        }
        return expression.find(target) != std::string_view::npos;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if not ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        const std::string target = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed_if.substr(7, trimmed_if.size() - 7 - 5)), nullptr);
        if (!IsValidLuaIdentifier(target) && !IsRenderedSimpleMemberPath(target)) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local || assign_lhs != target) {
            continue;
        }

        const std::string default_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (default_expression.empty() || default_expression == "nil" ||
            expression_uses_target(default_expression, target)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + target + " = (" + target + " or " + default_expression + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedDefaultAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }
        const std::string condition = std::string(
            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5)));

        std::vector<std::string> condition_parts;
        if (!SplitRenderedTopLevelBooleanChain(condition, "or", &condition_parts) ||
            condition_parts.size() != 2) {
            continue;
        }

        const std::string positive_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(condition_parts[0]), nullptr);
        const std::string negative_default =
            StripRenderedWholeParentheses(TrimLuaWhitespace(condition_parts[1]), nullptr);
        if (positive_condition.empty() || !StartsWithLuaKeyword(negative_default, "not")) {
            continue;
        }

        const std::string default_value = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(std::string_view(negative_default).substr(3)), nullptr);
        if (default_value.empty()) {
            continue;
        }
        const bool default_value_is_definitely_truthy =
            IsRenderedDefinitelyTruthyExpression(default_value);

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        if (assign_is_local) {
            if (!default_value_is_definitely_truthy) {
                continue;
            }
            bool used_after = false;
            for (std::size_t probe = line_index + 3;
                 probe < std::min(lines->size(), line_index + 7);
                 ++probe) {
                if (CountIdentifierUses((*lines)[probe], assign_lhs) != 0) {
                    used_after = true;
                    break;
                }
            }
            if (!used_after) {
                continue;
            }

            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          indent + "local " + assign_lhs + " = " + default_value);
            (*lines)[line_index + 1] = indent + "if " + positive_condition + " then";
            (*lines)[line_index + 2] = inner_indent + assign_lhs + " = " + assign_rhs;
            changed = true;
            ++line_index;
            continue;
        }

        if (line_index == 0) {
            continue;
        }

        std::vector<std::string> local_names;
        if (!ParseRenderedLocalDeclarationLine((*lines)[line_index - 1], &local_names) ||
            local_names.size() != 1 || local_names.front() != assign_lhs) {
            continue;
        }

        (*lines)[line_index - 1] = indent + "local " + assign_lhs + " = " + default_value;
        if (default_value_is_definitely_truthy) {
            (*lines)[line_index] = indent + "if " + positive_condition + " then";
        } else {
            (*lines)[line_index] =
                indent + "if " + positive_condition + " or not " + assign_lhs + " then";
        }
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaBooleanLoadPairs(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        std::vector<std::string> local_names;
        if (!ParseRenderedLocalDeclarationLine((*lines)[line_index], &local_names) ||
            local_names.size() != 1) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }
        const std::string condition = std::string(
            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5)));
        if (condition.empty()) {
            continue;
        }

        bool guarded_is_local = false;
        std::string guarded_lhs;
        std::string guarded_rhs;
        bool final_is_local = false;
        std::string final_lhs;
        std::string final_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &guarded_is_local,
                                               &guarded_lhs,
                                               &guarded_rhs) ||
            guarded_is_local || guarded_lhs != local_names.front() ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 4],
                                               &final_is_local,
                                               &final_lhs,
                                               &final_rhs) ||
            final_is_local || final_lhs != local_names.front()) {
            continue;
        }

        const std::string normalized_guarded_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(guarded_rhs), nullptr);
        const std::string normalized_final_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(final_rhs), nullptr);

        std::string initializer;
        if (normalized_guarded_rhs == "false" && normalized_final_rhs == "true") {
            bool negated_changed = false;
            initializer = NegateRenderedConditionExpression(condition, &negated_changed);
        } else if (normalized_guarded_rhs == "true" && normalized_final_rhs == "false") {
            initializer = NormalizeRenderedConditionExpression(condition, nullptr);
        } else {
            continue;
        }

        if (initializer.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "local " + local_names.front() + " = " + initializer;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 5));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaBooleanNumericLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string_view outer_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view inner_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!outer_if.starts_with("if ") || !outer_if.ends_with(" then") ||
            !inner_if.starts_with("if ") || !inner_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4])) != "end") {
            continue;
        }

        const std::string outer_condition = std::string(
            TrimLuaWhitespace(outer_if.substr(3, outer_if.size() - 3 - 5)));
        const std::string inner_condition = std::string(
            TrimLuaWhitespace(inner_if.substr(3, inner_if.size() - 3 - 5)));
        std::vector<std::string> outer_parts;
        std::vector<std::string> inner_parts;
        if (!SplitRenderedTopLevelBooleanChain(outer_condition, "or", &outer_parts) ||
            !SplitRenderedTopLevelBooleanChain(inner_condition, "or", &inner_parts) ||
            outer_parts.size() != 2 || inner_parts.size() != 2) {
            continue;
        }

        const std::string source_expression = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(inner_parts[0]),
            nullptr);
        if (source_expression.empty()) {
            continue;
        }

        const std::string_view inner_false_part = TrimLuaWhitespace(inner_parts[1]);
        const std::string_view outer_true_part = TrimLuaWhitespace(outer_parts[1]);
        const std::string normalized_outer_head = NormalizeRenderedConditionExpression(
            TrimLuaWhitespace(outer_parts[0]),
            nullptr);
        const std::string false_value =
            StartsWithLuaKeyword(inner_false_part, "not")
                ? StripRenderedWholeParentheses(TrimLuaWhitespace(inner_false_part.substr(3)),
                                                nullptr)
                : std::string();
        const std::string true_value =
            StartsWithLuaKeyword(outer_true_part, "not")
                ? StripRenderedWholeParentheses(TrimLuaWhitespace(outer_true_part.substr(3)),
                                                nullptr)
                : std::string();
        if (false_value.empty() || true_value.empty() ||
            normalized_outer_head != (source_expression + " ~= true")) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !assign_is_local ||
            NormalizeRenderedConditionOperand(assign_rhs, nullptr) != source_expression) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "local " + assign_lhs + " = ((" + source_expression +
                               " == true and " + true_value + ") or ((not " +
                               source_expression + " and " + false_value + ") or " +
                               source_expression + "))";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 5));
        changed = true;
    }

    return changed;
}

bool SplitRenderedTopLevelCommaChain(std::string_view value,
                                     std::vector<std::string>* parts) {
    if (parts == nullptr) {
        return false;
    }

    value = TrimLuaWhitespace(value);
    parts->clear();

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    std::size_t part_begin = 0;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const char ch = value[i];
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
        switch (ch) {
        case '(':
            ++paren_depth;
            break;
        case ')':
            if (paren_depth > 0) {
                --paren_depth;
            }
            break;
        case '{':
            ++brace_depth;
            break;
        case '}':
            if (brace_depth > 0) {
                --brace_depth;
            }
            break;
        case '[':
            ++bracket_depth;
            break;
        case ']':
            if (bracket_depth > 0) {
                --bracket_depth;
            }
            break;
        case ',':
            if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                parts->push_back(
                    std::string(TrimLuaWhitespace(value.substr(part_begin, i - part_begin))));
                part_begin = i + 1;
            }
            break;
        default:
            break;
        }
    }

    parts->push_back(std::string(TrimLuaWhitespace(value.substr(part_begin))));
    return true;
}

bool TryExtractRenderedIfCondition(std::string_view line, std::string* condition);

bool TryParseRenderedTrailingCall(std::string_view line,
                                  std::string_view* callee,
                                  std::string_view* arguments) {
    if (callee == nullptr || arguments == nullptr) {
        return false;
    }

    line = TrimLuaWhitespace(StripLuaLineComment(line));
    if (line.empty() || !line.ends_with(')')) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    std::size_t call_open = std::string_view::npos;
    for (std::size_t i = 0; i < line.size(); ++i) {
        const char ch = line[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < line.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < line.size()) {
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
        if (ch == '(' && brace_depth == 0 && bracket_depth == 0 && paren_depth == 0) {
            call_open = i;
        }
        if (ch == '(') {
            ++paren_depth;
            continue;
        }
        if (ch == ')') {
            paren_depth = std::max(0, paren_depth - 1);
            continue;
        }
    }

    if (call_open == std::string_view::npos || call_open == 0) {
        return false;
    }

    std::size_t close = std::string_view::npos;
    if (!FindRenderedMatchingParen(line, call_open, &close) || close + 1 != line.size()) {
        return false;
    }

    const std::string_view callee_view = TrimLuaWhitespace(line.substr(0, call_open));
    if (callee_view.empty() || callee_view.find('=') != std::string_view::npos) {
        return false;
    }

    *callee = callee_view;
    *arguments = line.substr(call_open + 1, close - call_open - 1);
    return true;
}

bool ParseRenderedSingleCallLine(std::string_view line,
                                 std::string* callee,
                                 std::vector<std::string>* arguments) {
    if (callee == nullptr || arguments == nullptr) {
        return false;
    }

    std::string_view callee_view;
    std::string_view arguments_view;
    if (!TryParseRenderedTrailingCall(line, &callee_view, &arguments_view)) {
        return false;
    }

    *callee = std::string(callee_view);
    return SplitRenderedTopLevelCommaChain(arguments_view, arguments);
}

bool ParseRenderedAssignmentLineAllowFunctionPlaceholders(
    std::string_view line,
    bool* is_local,
    std::vector<std::string>* lhs_names,
    std::string* rhs_expression) {
    if (lhs_names == nullptr || rhs_expression == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    if (is_local != nullptr) {
        *is_local = false;
    }
    if (trimmed.starts_with("local ")) {
        if (is_local != nullptr) {
            *is_local = true;
        }
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs.empty() || rhs.empty()) {
        return false;
    }

    std::vector<std::string> parsed_names;
    std::size_t cursor = 0;
    while (cursor < lhs.size()) {
        const std::size_t comma = lhs.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? lhs.substr(cursor)
                                            : lhs.substr(cursor, comma - cursor));
        if (part.empty()) {
            return false;
        }
        if (!IsValidLuaIdentifier(part) && part != "function()" && part != "function ()") {
            return false;
        }
        parsed_names.emplace_back(part);
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }

    if (parsed_names.empty()) {
        return false;
    }

    *lhs_names = std::move(parsed_names);
    *rhs_expression = std::string(rhs);
    return true;
}

#include "../rendered_lua_text_call_cleanup.inc"

bool TryBuildRenderedPositiveFallbackExpression(std::string_view condition,
                                                std::string* positive_expression) {
    if (positive_expression == nullptr) {
        return false;
    }

    condition = TrimLuaWhitespace(condition);
    if (condition.empty()) {
        return false;
    }

    std::vector<std::string> or_parts;
    if (!SplitRenderedTopLevelBooleanChain(condition, "or", &or_parts)) {
        or_parts = {std::string(condition)};
    }
    if (or_parts.empty()) {
        return false;
    }

    std::string rebuilt;
    for (const std::string& raw_part : or_parts) {
        const std::string_view part = TrimLuaWhitespace(raw_part);
        if (!StartsWithLuaKeyword(part, "not")) {
            return false;
        }

        const std::string positive = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(part.substr(3)),
            nullptr);
        if (positive.empty()) {
            return false;
        }

        if (rebuilt.empty()) {
            rebuilt = positive;
        } else {
            rebuilt = RenderLogicalExpression(rebuilt, "and", positive);
        }
    }

    if (rebuilt.empty()) {
        return false;
    }

    *positive_expression = rebuilt;
    return true;
}
