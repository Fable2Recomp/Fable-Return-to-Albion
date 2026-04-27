bool NormalizeRenderedLuaMissingDottedFieldSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *is_local_out = false;
            lhs_out->clear();
            rhs_out->clear();

            bool parsed_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &parsed_is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = parsed_is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }

            std::string lhs = std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            bool is_local = false;
            if (lhs.starts_with("local ")) {
                is_local = true;
                lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs).substr(6)));
            }
            if (lhs.empty()) {
                return false;
            }

            *is_local_out = is_local;
            *lhs_out = std::move(lhs);
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(parsed_rhs));
            return true;
        };

    const auto parse_seed_base =
        [](std::string_view lhs, std::string* base_out) -> bool {
            if (base_out == nullptr) {
                return false;
            }
            base_out->clear();

            lhs = refii::lua::TrimLuaWhitespace(lhs);
            if (lhs.empty() || lhs.find('(') != std::string_view::npos ||
                lhs.find(')') != std::string_view::npos) {
                return false;
            }

            const std::size_t last_dot = lhs.rfind('.');
            if (last_dot == std::string_view::npos || last_dot == 0) {
                return false;
            }

            const std::string base =
                std::string(refii::lua::TrimLuaWhitespace(lhs.substr(0, last_dot)));
            if (base.empty() || base.find('.') == std::string::npos ||
                base.find('[') != std::string::npos || base.find(']') != std::string::npos) {
                return false;
            }

            std::size_t start = 0;
            while (start < base.size()) {
                const std::size_t dot = base.find('.', start);
                const std::size_t end = dot == std::string::npos ? base.size() : dot;
                const std::string_view segment = std::string_view(base).substr(start, end - start);
                if (segment.empty() || !refii::lua::IsValidLuaIdentifier(segment)) {
                    return false;
                }
                if (dot == std::string::npos) {
                    break;
                }
                start = dot + 1;
            }

            *base_out = base;
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!parse_single_assignment((*lines)[line_index], &is_local, &lhs, &rhs) ||
                is_local) {
                continue;
            }

            std::string seed_base;
            if (!parse_seed_base(lhs, &seed_base)) {
                continue;
            }

            bool already_seeded = false;
            std::size_t scanned_non_empty = 0;
            for (std::size_t probe = line_index; probe > 0 && scanned_non_empty < 256;) {
                --probe;
                const std::string_view stripped =
                    refii::lua::StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    continue;
                }
                ++scanned_non_empty;
                if (trimmed == "end") {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (parse_single_assignment((*lines)[probe],
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs) &&
                    probe_lhs == seed_base) {
                    already_seeded = true;
                    break;
                }
            }
            if (already_seeded) {
                continue;
            }

            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          indent + seed_base + " = {}");
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaCombatStylesIndexedAliasWrites(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *is_local_out = false;
            lhs_out->clear();
            rhs_out->clear();

            bool parsed_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &parsed_is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = parsed_is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }

            std::string lhs = std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            bool is_local = false;
            if (lhs.starts_with("local ")) {
                is_local = true;
                lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs).substr(6)));
            }
            if (lhs.empty()) {
                return false;
            }

            *is_local_out = is_local;
            *lhs_out = std::move(lhs);
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(parsed_rhs));
            return true;
        };

    const auto parse_combatstyles_index_alias =
        [](std::string_view lhs, std::string* alias_out) -> bool {
            if (alias_out == nullptr) {
                return false;
            }
            alias_out->clear();

            lhs = refii::lua::TrimLuaWhitespace(lhs);
            constexpr std::string_view kPrefix = "CombatStyles[";
            if (!lhs.starts_with(kPrefix) || lhs.size() <= (kPrefix.size() + 1) ||
                lhs.back() != ']') {
                return false;
            }
            const std::string_view key =
                refii::lua::TrimLuaWhitespace(lhs.substr(kPrefix.size(),
                                                         lhs.size() - kPrefix.size() - 1));
            if (!refii::lua::IsValidLuaIdentifier(key) ||
                !refii::lua::IsRenderTemporaryName(key)) {
                return false;
            }
            *alias_out = std::string(key);
            return true;
        };

    const auto parse_combatstyles_dotted_base =
        [](std::string_view lhs, std::string* base_out) -> bool {
            if (base_out == nullptr) {
                return false;
            }
            base_out->clear();

            lhs = refii::lua::TrimLuaWhitespace(lhs);
            constexpr std::string_view kPrefix = "CombatStyles.";
            if (!lhs.starts_with(kPrefix)) {
                return false;
            }
            const std::size_t next_dot = lhs.find('.', kPrefix.size());
            if (next_dot == std::string_view::npos) {
                const std::string_view name = lhs.substr(kPrefix.size());
                if (!refii::lua::IsValidLuaIdentifier(name)) {
                    return false;
                }
                *base_out = std::string(lhs);
                return true;
            }
            if (next_dot <= kPrefix.size()) {
                return false;
            }
            const std::string_view name =
                lhs.substr(kPrefix.size(), next_dot - kPrefix.size());
            if (!refii::lua::IsValidLuaIdentifier(name)) {
                return false;
            }
            *base_out = std::string(lhs.substr(0, next_dot));
            return true;
        };

    const auto replace_all =
        [](std::string* line,
           const std::string& needle,
           const std::string& replacement) -> bool {
            if (line == nullptr || needle.empty()) {
                return false;
            }
            bool replaced = false;
            std::size_t cursor = 0;
            while (true) {
                const std::size_t found = line->find(needle, cursor);
                if (found == std::string::npos) {
                    break;
                }
                line->replace(found, needle.size(), replacement);
                cursor = found + replacement.size();
                replaced = true;
            }
            return replaced;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 32);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!parse_single_assignment((*lines)[line_index], &is_local, &lhs, &rhs) ||
                is_local || rhs != "{}") {
                continue;
            }

            std::string alias_identifier;
            if (!parse_combatstyles_index_alias(lhs, &alias_identifier)) {
                continue;
            }

            std::size_t next_non_empty = line_index + 1;
            while (next_non_empty < lines->size()) {
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[next_non_empty]));
                if (!trimmed.empty()) {
                    break;
                }
                ++next_non_empty;
            }
            if (next_non_empty >= lines->size()) {
                continue;
            }

            bool next_is_local = false;
            std::string next_lhs;
            std::string next_rhs;
            if (!parse_single_assignment((*lines)[next_non_empty],
                                         &next_is_local,
                                         &next_lhs,
                                         &next_rhs) ||
                next_is_local) {
                continue;
            }

            std::string dotted_base;
            if (!parse_combatstyles_dotted_base(next_lhs, &dotted_base)) {
                continue;
            }

            const std::string indexed_base = "CombatStyles[" + alias_identifier + "]";
            bool local_changed = false;
            local_changed |= replace_all(&(*lines)[line_index], indexed_base, dotted_base);

            const std::size_t replace_limit = std::min(lines->size(), line_index + 192);
            for (std::size_t probe = line_index + 1; probe < replace_limit; ++probe) {
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (parse_single_assignment((*lines)[probe],
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs) &&
                    probe_lhs == alias_identifier) {
                    break;
                }

                local_changed |= replace_all(&(*lines)[probe], indexed_base, dotted_base);
            }

            if (!local_changed) {
                continue;
            }

            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            const std::string_view stripped =
                refii::lua::StripLuaLineComment((*lines)[line_index]);
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
            if (trimmed.empty()) {
                continue;
            }

            const std::size_t open = trimmed.find("CombatStyles[");
            if (open == std::string_view::npos) {
                continue;
            }
            const std::size_t key_begin = open + std::string_view("CombatStyles[").size();
            const std::size_t close = trimmed.find(']', key_begin);
            if (close == std::string_view::npos || close <= key_begin) {
                continue;
            }

            const std::string alias_identifier = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(key_begin, close - key_begin)));
            if (!refii::lua::IsValidLuaIdentifier(alias_identifier) ||
                !refii::lua::IsRenderTemporaryName(alias_identifier)) {
                continue;
            }

            std::string anchored_base;
            std::size_t scanned_non_empty = 0;
            for (std::size_t probe = line_index; probe > 0 && scanned_non_empty < 24;) {
                --probe;
                const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[probe]));
                if (probe_trimmed.empty()) {
                    continue;
                }
                ++scanned_non_empty;
                if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (!parse_single_assignment((*lines)[probe],
                                             &probe_is_local,
                                             &probe_lhs,
                                             &probe_rhs)) {
                    continue;
                }
                if (!probe_is_local && probe_lhs == alias_identifier) {
                    break;
                }
                if (probe_rhs != "{}") {
                    continue;
                }
                if (!parse_combatstyles_dotted_base(probe_lhs, &anchored_base)) {
                    continue;
                }
                break;
            }
            if (anchored_base.empty()) {
                continue;
            }

            const std::string indexed_base = "CombatStyles[" + alias_identifier + "]";
            if (!replace_all(&(*lines)[line_index], indexed_base, anchored_base)) {
                continue;
            }

            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaFieldNilTablePlaceholdersBeforeWrites(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *is_local_out = false;
            lhs_out->clear();
            rhs_out->clear();

            bool parsed_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &parsed_is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = parsed_is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }

            std::string lhs = std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            bool is_local = false;
            if (lhs.starts_with("local ")) {
                is_local = true;
                lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs).substr(6)));
            }
            if (lhs.empty()) {
                return false;
            }

            *is_local_out = is_local;
            *lhs_out = std::move(lhs);
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(parsed_rhs));
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!parse_single_assignment((*lines)[line_index], &is_local, &lhs, &rhs) ||
                is_local || rhs != "nil" || lhs.find('.') == std::string::npos ||
                lhs.find('(') != std::string::npos || lhs.find(')') != std::string::npos) {
                continue;
            }

            if (line_index > 0) {
                std::size_t previous_non_empty = line_index;
                while (previous_non_empty > 0) {
                    --previous_non_empty;
                    const std::string_view previous_trimmed = refii::lua::TrimLuaWhitespace(
                        refii::lua::StripLuaLineComment((*lines)[previous_non_empty]));
                    if (previous_trimmed.empty()) {
                        continue;
                    }
                    if (previous_trimmed != "end") {
                        previous_non_empty = lines->size();
                    }
                    break;
                }
                if (previous_non_empty == lines->size()) {
                    continue;
                }
            }

            bool observed_field_or_index_write = false;
            std::size_t lookahead_non_empty = 0;
            for (std::size_t probe = line_index + 1;
                 probe < lines->size() && lookahead_non_empty < 96;
                 ++probe) {
                const std::string_view stripped =
                    refii::lua::StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    continue;
                }
                ++lookahead_non_empty;

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (parse_single_assignment((*lines)[probe],
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs)) {
                    if (probe_lhs == lhs) {
                        break;
                    }
                    if (probe_lhs.starts_with(lhs + ".") || probe_lhs.starts_with(lhs + "[")) {
                        observed_field_or_index_write = true;
                        break;
                    }
                }
            }
            if (!observed_field_or_index_write) {
                continue;
            }

            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            (*lines)[line_index] = indent + lhs + " = {}";
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaLiteralNilFieldPlaceholdersBeforeWrites(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto split_top_level_commas =
        [](std::string_view text, std::vector<std::string>* tokens_out) -> bool {
            if (tokens_out == nullptr) {
                return false;
            }
            tokens_out->clear();

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t token_start = 0;
            for (std::size_t cursor = 0; cursor < text.size(); ++cursor) {
                const char ch = text[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < text.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < text.size()) {
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
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0 || ch != ',') {
                    continue;
                }
                tokens_out->push_back(std::string(
                    refii::lua::TrimLuaWhitespace(text.substr(token_start, cursor - token_start))));
                token_start = cursor + 1;
            }
            tokens_out->push_back(
                std::string(refii::lua::TrimLuaWhitespace(text.substr(token_start))));
            return true;
        };

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *is_local_out = false;
            lhs_out->clear();
            rhs_out->clear();

            bool parsed_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &parsed_is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = parsed_is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }

            std::string lhs = std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            bool is_local = false;
            if (lhs.starts_with("local ")) {
                is_local = true;
                lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs).substr(6)));
            }
            if (lhs.empty()) {
                return false;
            }

            *is_local_out = is_local;
            *lhs_out = std::move(lhs);
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(parsed_rhs));
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!parse_single_assignment((*lines)[line_index], &is_local, &lhs, &rhs) ||
                lhs.find('(') != std::string::npos || lhs.find(')') != std::string::npos) {
                continue;
            }

            const std::string rhs_trimmed = std::string(refii::lua::TrimLuaWhitespace(rhs));
            if (rhs_trimmed.size() < 2 || rhs_trimmed.front() != '{' ||
                rhs_trimmed.back() != '}') {
                continue;
            }

            std::vector<std::string> tokens;
            split_top_level_commas(
                std::string_view(rhs_trimmed).substr(1, rhs_trimmed.size() - 2), &tokens);
            if (tokens.empty()) {
                continue;
            }

            std::unordered_set<std::string> nil_fields;
            for (const std::string& token : tokens) {
                const std::size_t eq = token.find('=');
                if (eq == std::string::npos || eq == 0 || eq + 1 >= token.size()) {
                    continue;
                }
                const std::string key = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(token).substr(0, eq)));
                const std::string value = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(token).substr(eq + 1)));
                if (refii::lua::IsValidLuaIdentifier(key) && value == "nil") {
                    nil_fields.insert(key);
                }
            }
            if (nil_fields.empty()) {
                continue;
            }

            std::unordered_set<std::string> fields_to_seed;
            std::size_t lookahead_non_empty = 0;
            for (std::size_t probe = line_index + 1;
                 probe < lines->size() && lookahead_non_empty < 96;
                 ++probe) {
                const std::string_view stripped =
                    refii::lua::StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    continue;
                }
                ++lookahead_non_empty;

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (parse_single_assignment((*lines)[probe],
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs) &&
                    probe_lhs == lhs) {
                    break;
                }

                for (const std::string& field : nil_fields) {
                    const std::string prefix = lhs + "." + field;
                    if (trimmed.starts_with(prefix + ".") || trimmed.starts_with(prefix + "[")) {
                        fields_to_seed.insert(field);
                    }
                }
                if (fields_to_seed.size() == nil_fields.size()) {
                    break;
                }
            }
            if (fields_to_seed.empty()) {
                continue;
            }

            bool token_changed = false;
            for (std::string& token : tokens) {
                const std::size_t eq = token.find('=');
                if (eq == std::string::npos || eq == 0 || eq + 1 >= token.size()) {
                    continue;
                }
                const std::string key = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(token).substr(0, eq)));
                if (!fields_to_seed.contains(key)) {
                    continue;
                }
                const std::string value = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(token).substr(eq + 1)));
                if (value != "nil") {
                    continue;
                }
                token = key + " = {}";
                token_changed = true;
            }
            if (!token_changed) {
                continue;
            }

            std::string rebuilt_rhs = "{";
            for (std::size_t token_index = 0; token_index < tokens.size(); ++token_index) {
                if (token_index > 0) {
                    rebuilt_rhs += ", ";
                }
                rebuilt_rhs += tokens[token_index];
            }
            rebuilt_rhs += "}";

            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            (*lines)[line_index] =
                indent + (is_local ? "local " : std::string()) + lhs + " = " + rebuilt_rhs;
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaCallFieldTableAliasSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line,
           bool* is_local_out,
           std::string* lhs_out,
           std::string* rhs_out) -> bool {
            if (is_local_out == nullptr || lhs_out == nullptr || rhs_out == nullptr) {
                return false;
            }
            *is_local_out = false;
            lhs_out->clear();
            rhs_out->clear();

            bool parsed_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &parsed_is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *is_local_out = parsed_is_local;
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
                return !lhs_out->empty() && !rhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }
            std::string normalized_lhs =
                std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            bool normalized_is_local = false;
            if (normalized_lhs.starts_with("local ")) {
                normalized_is_local = true;
                normalized_lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(normalized_lhs).substr(6)));
            }
            *is_local_out = normalized_is_local;
            *lhs_out = std::move(normalized_lhs);
            *rhs_out = std::string(refii::lua::TrimLuaWhitespace(parsed_rhs));
            return !lhs_out->empty() && !rhs_out->empty();
        };

    const auto parse_call_field_lhs =
        [](std::string_view lhs,
           std::string* call_expression_out,
           std::string* field_out) -> bool {
            if (call_expression_out == nullptr || field_out == nullptr) {
                return false;
            }
            call_expression_out->clear();
            field_out->clear();

            lhs = refii::lua::TrimLuaWhitespace(lhs);
            if (lhs.empty()) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t split = std::string_view::npos;
            for (std::size_t cursor = 0; cursor < lhs.size(); ++cursor) {
                const char ch = lhs[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < lhs.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < lhs.size()) {
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
                if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 && ch == '.') {
                    split = cursor;
                }
            }

            if (split == std::string_view::npos || split == 0 || split + 1 >= lhs.size()) {
                return false;
            }

            const std::string call_expression =
                std::string(refii::lua::TrimLuaWhitespace(lhs.substr(0, split)));
            const std::string field_name =
                std::string(refii::lua::TrimLuaWhitespace(lhs.substr(split + 1)));
            if (call_expression.empty() || field_name.empty() ||
                !refii::lua::IsValidLuaIdentifier(field_name) ||
                call_expression.find('(') == std::string::npos) {
                return false;
            }

            *call_expression_out = call_expression;
            *field_out = field_name;
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 48);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!parse_single_assignment((*lines)[line_index], &is_local, &lhs, &rhs) ||
                is_local || (rhs != "{}" && rhs != "{ }")) {
                continue;
            }

            std::string call_expression;
            std::string field_name;
            if (!parse_call_field_lhs(lhs, &call_expression, &field_name) ||
                refii::lua::IsRenderTemporaryName(field_name)) {
                continue;
            }

            bool has_field_uses = false;
            std::size_t lookahead_non_empty = 0;
            for (std::size_t probe = line_index + 1;
                 probe < lines->size() && lookahead_non_empty < 96;
                 ++probe) {
                const std::string_view stripped =
                    refii::lua::StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    continue;
                }
                ++lookahead_non_empty;
                if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }
                if (trimmed.starts_with("local " + field_name)) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (parse_single_assignment((*lines)[probe],
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs) &&
                    probe_lhs == field_name) {
                    break;
                }

                if (refii::lua::CountIdentifierUses(stripped, field_name) != 0) {
                    has_field_uses = true;
                    break;
                }
            }
            if (!has_field_uses) {
                continue;
            }

            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            (*lines)[line_index] = indent + "local " + field_name + " = {}";
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                          indent + call_expression + "." + field_name + " = " + field_name);
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaLocalLiteralMissingFieldSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto split_top_level_commas =
        [](std::string_view text, std::vector<std::string>* tokens_out) -> bool {
            if (tokens_out == nullptr) {
                return false;
            }
            tokens_out->clear();

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t token_start = 0;
            for (std::size_t cursor = 0; cursor < text.size(); ++cursor) {
                const char ch = text[cursor];
                if (in_single_quote) {
                    if (ch == '\\' && cursor + 1 < text.size()) {
                        ++cursor;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && cursor + 1 < text.size()) {
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
                if (ch != ',') {
                    continue;
                }
                tokens_out->push_back(std::string(
                    refii::lua::TrimLuaWhitespace(text.substr(token_start, cursor - token_start))));
                token_start = cursor + 1;
            }
            tokens_out->push_back(
                std::string(refii::lua::TrimLuaWhitespace(text.substr(token_start))));
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 48);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                         &is_local,
                                                         &lhs_names,
                                                         &rhs_expression) ||
                !is_local || lhs_names.size() != 1) {
                continue;
            }

            const std::string local_name =
                std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
            const std::string rhs_trimmed =
                std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
            if (!refii::lua::IsRenderTemporaryName(local_name) || rhs_trimmed.size() < 2 ||
                rhs_trimmed.front() != '{' || rhs_trimmed.back() != '}') {
                continue;
            }

            std::unordered_set<std::string> literal_fields;
            std::vector<std::string> tokens;
            if (split_top_level_commas(std::string_view(rhs_trimmed).substr(
                                           1, rhs_trimmed.size() - 2),
                                       &tokens)) {
                for (const std::string& token : tokens) {
                    const std::size_t eq = token.find('=');
                    if (eq == std::string::npos || eq == 0) {
                        continue;
                    }
                    const std::string key = std::string(
                        refii::lua::TrimLuaWhitespace(std::string_view(token).substr(0, eq)));
                    if (refii::lua::IsValidLuaIdentifier(key)) {
                        literal_fields.insert(key);
                    }
                }
            }

            std::string missing_field;
            for (std::size_t probe = line_index + 1;
                 probe < lines->size() && probe <= line_index + 48;
                 ++probe) {
                const std::string_view trimmed_probe = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed_probe.empty()) {
                    continue;
                }
                if (refii::lua::IsRenderedFunctionStartLine(trimmed_probe)) {
                    break;
                }

                if (trimmed_probe.starts_with("local " + local_name + " =") ||
                    trimmed_probe.starts_with(local_name + " =")) {
                    break;
                }

                const std::string prefix = local_name + ".";
                if (!trimmed_probe.starts_with(prefix)) {
                    continue;
                }

                const std::string_view suffix = trimmed_probe.substr(prefix.size());
                const std::size_t equal = suffix.find('=');
                if (equal == std::string_view::npos || equal == 0) {
                    continue;
                }

                const std::string lhs_path = std::string(
                    refii::lua::TrimLuaWhitespace(suffix.substr(0, equal)));
                if (lhs_path.empty()) {
                    continue;
                }

                std::size_t field_end = std::string::npos;
                const std::size_t bracket = lhs_path.find('[');
                const std::size_t dot = lhs_path.find('.');
                if (bracket != std::string::npos && bracket > 0) {
                    field_end = bracket;
                } else if (dot != std::string::npos && dot > 0) {
                    field_end = dot;
                } else {
                    continue;
                }

                const std::string field = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs_path).substr(0, field_end)));
                if (field.empty() || !refii::lua::IsValidLuaIdentifier(field) ||
                    literal_fields.contains(field)) {
                    continue;
                }

                missing_field = field;
                break;
            }
            if (missing_field.empty()) {
                continue;
            }

            const std::string_view stripped = refii::lua::StripLuaLineComment((*lines)[line_index]);
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();

            const std::string literal_body = std::string(
                refii::lua::TrimLuaWhitespace(std::string_view(rhs_trimmed).substr(
                    1, rhs_trimmed.size() - 2)));
            const std::string rewritten_rhs =
                literal_body.empty()
                    ? ("{" + missing_field + " = {}}")
                    : ("{" + literal_body + ", " + missing_field + " = {}}");
            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            (*lines)[line_index] =
                indent + "local " + local_name + " = " + rewritten_rhs + std::string(comment);
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaTemporaryIndexedFieldSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string local_name;
            {
                std::vector<std::string> lhs_names;
                std::string rhs_expression;
                if (refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                            &is_local,
                                                            &lhs_names,
                                                            &rhs_expression) &&
                    is_local && lhs_names.size() == 1) {
                    local_name =
                        std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                } else {
                    std::string indent;
                    std::string parsed_lhs;
                    std::string parsed_rhs;
                    if (!ParseSimpleRenderedAssignment((*lines)[line_index],
                                                      &indent,
                                                      &parsed_lhs,
                                                      &parsed_rhs)) {
                        continue;
                    }
                    std::string normalized_lhs =
                        std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
                    if (!normalized_lhs.starts_with("local ")) {
                        continue;
                    }
                    is_local = true;
                    local_name = std::string(refii::lua::TrimLuaWhitespace(
                        std::string_view(normalized_lhs).substr(6)));
                    if (local_name.empty() ||
                        std::string(refii::lua::TrimLuaWhitespace(parsed_rhs)).empty() ||
                        !std::string(refii::lua::TrimLuaWhitespace(parsed_rhs)).starts_with("{")) {
                        continue;
                    }
                }
            }

            if (!is_local || !refii::lua::IsRenderTemporaryName(local_name)) {
                continue;
            }

            const std::string prefix = local_name + ".";
            std::size_t indexed_field_line = lines->size();
            std::string indexed_field_name;
            const std::size_t lookahead_limit = std::min(lines->size(), line_index + 48);
            for (std::size_t probe = line_index + 1; probe < lookahead_limit; ++probe) {
                const std::string_view stripped =
                    refii::lua::StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    continue;
                }
                if (refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }

                bool probe_is_local = false;
                std::vector<std::string> probe_lhs_names;
                std::string probe_rhs_expression;
                if (refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                            &probe_is_local,
                                                            &probe_lhs_names,
                                                            &probe_rhs_expression) &&
                    probe_lhs_names.size() == 1) {
                    const std::string probe_lhs = std::string(
                        refii::lua::TrimLuaWhitespace(probe_lhs_names.front()));
                    if (probe_lhs == local_name) {
                        break;
                    }
                }

                if (!trimmed.starts_with(prefix)) {
                    continue;
                }
                const std::string_view suffix = trimmed.substr(prefix.size());
                const std::size_t equal = suffix.find('=');
                if (equal == std::string_view::npos || equal == 0) {
                    continue;
                }

                const std::string lhs_path = std::string(
                    refii::lua::TrimLuaWhitespace(suffix.substr(0, equal)));
                if (lhs_path.empty()) {
                    continue;
                }

                std::size_t field_end = std::string::npos;
                const std::size_t bracket = lhs_path.find('[');
                const std::size_t dot = lhs_path.find('.');
                if (bracket != std::string::npos && bracket > 0) {
                    field_end = bracket;
                } else if (dot != std::string::npos && dot > 0) {
                    field_end = dot;
                } else {
                    continue;
                }

                const std::string field_name = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs_path).substr(0, field_end)));
                if (!refii::lua::IsValidLuaIdentifier(field_name)) {
                    continue;
                }

                indexed_field_line = probe;
                indexed_field_name = field_name;
                break;
            }

            if (indexed_field_line >= lines->size() || indexed_field_name.empty()) {
                continue;
            }

            bool already_seeded = false;
            const std::string seed_prefix = local_name + "." + indexed_field_name;
            for (std::size_t probe = line_index + 1; probe < indexed_field_line; ++probe) {
                const std::string_view trimmed =
                    refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                if (trimmed.starts_with(seed_prefix + " =")) {
                    already_seeded = true;
                    break;
                }
            }
            if (already_seeded) {
                continue;
            }

            const std::string indent = GetLuaLineIndent((*lines)[indexed_field_line]);
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(indexed_field_line),
                          indent + seed_prefix + " = {}");
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaTemporaryNestedFieldSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line, std::string* lhs_out) -> bool {
            if (lhs_out == nullptr) {
                return false;
            }
            lhs_out->clear();

            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                return !lhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }
            std::string lhs = std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            if (lhs.starts_with("local ")) {
                lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs).substr(6)));
            }
            if (lhs.empty()) {
                return false;
            }
            *lhs_out = std::move(lhs);
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string lhs;
            if (!parse_single_assignment((*lines)[line_index], &lhs)) {
                continue;
            }

            const std::size_t first_dot = lhs.find('.');
            if (first_dot == std::string::npos || first_dot == 0) {
                continue;
            }
            const std::string root = lhs.substr(0, first_dot);
            if (!refii::lua::IsRenderTemporaryName(root)) {
                continue;
            }

            const std::string_view tail = std::string_view(lhs).substr(first_dot + 1);
            if (tail.empty()) {
                continue;
            }
            const std::size_t bracket = tail.find('[');
            const std::size_t dot = tail.find('.');
            std::size_t field_end = std::string::npos;
            if (bracket != std::string::npos && bracket > 0) {
                field_end = bracket;
            } else if (dot != std::string::npos && dot > 0) {
                field_end = dot;
            } else {
                continue;
            }
            const std::string field =
                std::string(refii::lua::TrimLuaWhitespace(tail.substr(0, field_end)));
            if (!refii::lua::IsValidLuaIdentifier(field)) {
                continue;
            }

            const std::string seed_lhs = root + "." + field;
            bool root_seen = false;
            bool seed_seen = false;
            std::size_t scanned_non_empty = 0;
            for (std::size_t probe = line_index; probe > 0 && scanned_non_empty < 128;) {
                --probe;
                const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[probe]));
                if (probe_trimmed.empty()) {
                    continue;
                }
                ++scanned_non_empty;
                if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                    break;
                }

                std::string probe_lhs;
                if (!parse_single_assignment((*lines)[probe], &probe_lhs)) {
                    continue;
                }
                if (probe_lhs == seed_lhs) {
                    seed_seen = true;
                    break;
                }
                if (probe_lhs == root) {
                    root_seen = true;
                    break;
                }
            }
            if (seed_seen || !root_seen) {
                continue;
            }

            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          indent + seed_lhs + " = {}");
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaCombatStylesNestedFieldSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_single_assignment =
        [](std::string_view line, std::string* lhs_out) -> bool {
            if (lhs_out == nullptr) {
                return false;
            }
            lhs_out->clear();

            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (refii::lua::ParseRenderedAssignmentLine(
                    line, &is_local, &lhs_names, &rhs_expression) &&
                lhs_names.size() == 1) {
                *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
                return !lhs_out->empty();
            }

            std::string indent;
            std::string parsed_lhs;
            std::string parsed_rhs;
            if (!ParseSimpleRenderedAssignment(
                    std::string(line), &indent, &parsed_lhs, &parsed_rhs)) {
                return false;
            }
            std::string lhs = std::string(refii::lua::TrimLuaWhitespace(parsed_lhs));
            if (lhs.starts_with("local ")) {
                lhs = std::string(
                    refii::lua::TrimLuaWhitespace(std::string_view(lhs).substr(6)));
            }
            if (lhs.empty()) {
                return false;
            }
            *lhs_out = std::move(lhs);
            return true;
        };

    bool changed = false;
    const int max_passes = ComputeAdaptiveNormalizationPassLimit(lines->size(), 64);
    changed |= RunBoundedNormalizationPassLoop(max_passes, [&](int /*pass*/) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string lhs;
            if (!parse_single_assignment((*lines)[line_index], &lhs) ||
                !lhs.starts_with("CombatStyles.")) {
                continue;
            }

            const std::string_view suffix = std::string_view(lhs).substr(13);
            const std::size_t name_end = suffix.find('.');
            if (name_end == std::string_view::npos || name_end == 0) {
                continue;
            }
            const std::string style_name = std::string(suffix.substr(0, name_end));
            if (!refii::lua::IsValidLuaIdentifier(style_name)) {
                continue;
            }

            const std::string_view remaining = suffix.substr(name_end + 1);
            const std::size_t bracket = remaining.find('[');
            const std::size_t dot = remaining.find('.');
            std::size_t field_end = std::string::npos;
            if (bracket != std::string::npos && bracket > 0) {
                field_end = bracket;
            } else if (dot != std::string::npos && dot > 0) {
                field_end = dot;
            } else {
                continue;
            }
            const std::string nested_field =
                std::string(refii::lua::TrimLuaWhitespace(remaining.substr(0, field_end)));
            if (!refii::lua::IsValidLuaIdentifier(nested_field)) {
                continue;
            }

            const std::string style_base = "CombatStyles." + style_name;
            const std::string seed_lhs = style_base + "." + nested_field;
            bool style_seen = false;
            bool seed_seen = false;
            std::size_t scanned_non_empty = 0;
            for (std::size_t probe = line_index; probe > 0 && scanned_non_empty < 192;) {
                --probe;
                const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                    refii::lua::StripLuaLineComment((*lines)[probe]));
                if (probe_trimmed.empty()) {
                    continue;
                }
                ++scanned_non_empty;
                if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                    break;
                }

                std::string probe_lhs;
                if (!parse_single_assignment((*lines)[probe], &probe_lhs)) {
                    continue;
                }
                if (probe_lhs == seed_lhs) {
                    seed_seen = true;
                    break;
                }
                if (probe_lhs == style_base) {
                    style_seen = true;
                    break;
                }
            }
            if (seed_seen || !style_seen) {
                continue;
            }

            const std::string indent = GetLuaLineIndent((*lines)[line_index]);
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          indent + seed_lhs + " = {}");
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool NormalizeRenderedLuaPermanentsTableHelperGuards(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view stripped =
            refii::lua::StripLuaLineComment((*lines)[line_index]);
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        if (trimmed ==
            "if type(k) == \"string\" and type(k) == \"function\" and PlutoPermanentsSaveTable[v] then") {
            (*lines)[line_index] = "    if type(k) == \"string\" and type(v) == \"function\" and PlutoPermanentsSaveTable[v] then";
            changed = true;
            continue;
        }

        if (trimmed == "if type(k) == \"string\" and type(k) == \"function\" then") {
            (*lines)[line_index] =
                "    if type(k) == \"string\" and type(v) == \"function\" then";
            changed = true;
            continue;
        }

        if (!trimmed.starts_with("AddFunctionsInTableToPermanentsTables = function(")) {
            continue;
        }

        bool already_guarded = false;
        for (std::size_t probe = line_index + 1;
             probe < lines->size() && probe <= line_index + 10;
             ++probe) {
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe_trimmed == "if in_table == nil then") {
                already_guarded = true;
            }
            break;
        }
        if (already_guarded) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + "  if in_table == nil then");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2),
                      indent + "  return");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3),
                      indent + "  end");
        changed = true;
        line_index += 3;
    }

    return changed;
}

bool NormalizeRenderedLuaSpecialChestGlobalSeed(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[line_index]));
        if (!trimmed.starts_with("function SpecialChest:")) {
            continue;
        }

        bool already_seeded = false;
        std::size_t scanned_non_empty = 0;
        for (std::size_t probe = line_index; probe > 0 && scanned_non_empty < 64;) {
            --probe;
            const std::string_view probe_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            ++scanned_non_empty;
            if (probe_trimmed.starts_with("SpecialChest =")) {
                already_seeded = true;
                break;
            }
            if (refii::lua::IsRenderedFunctionStartLine(probe_trimmed)) {
                break;
            }
        }
        if (already_seeded) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      indent + "SpecialChest = SpecialChest or {}");
        changed = true;
        ++line_index;
    }

    return changed;
}

enum class RenderedLuaSeedNormalizationLevel {
    kHazard,
    kStandard,
    kExtended,
};

struct RenderedLuaSeedHints {
    bool has_dot = false;
    bool has_index = false;
    bool has_nil = false;
    bool has_combatstyles = false;
    bool has_special_chest = false;
    bool has_permanents = false;
};

RenderedLuaSeedHints CollectRenderedLuaSeedHints(const std::vector<std::string>& lines) {
    RenderedLuaSeedHints hints{};
    for (const std::string& line : lines) {
        const std::string_view stripped = refii::lua::StripLuaLineComment(line);
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }
        hints.has_dot = hints.has_dot || trimmed.find('.') != std::string_view::npos;
        hints.has_index = hints.has_index || trimmed.find('[') != std::string_view::npos;
        hints.has_nil = hints.has_nil || trimmed.find("nil") != std::string_view::npos;
        hints.has_combatstyles =
            hints.has_combatstyles || trimmed.find("CombatStyles") != std::string_view::npos;
        hints.has_special_chest =
            hints.has_special_chest || trimmed.find("SpecialChest") != std::string_view::npos;
        hints.has_permanents = hints.has_permanents ||
                               trimmed.find("PlutoPermanents") != std::string_view::npos ||
                               trimmed.find("tables_already_added") != std::string_view::npos ||
                               trimmed.find("tables_already_removed") != std::string_view::npos ||
                               trimmed.find("AddFunctionsInTableToPermanentsTables") !=
                                   std::string_view::npos;

        if (hints.has_dot && hints.has_index && hints.has_nil && hints.has_combatstyles &&
            hints.has_special_chest && hints.has_permanents) {
            break;
        }
    }
    return hints;
}

bool ApplyRenderedLuaSeedNormalizations(
    std::vector<std::string>* lines,
    RenderedLuaSeedNormalizationLevel level) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const RenderedLuaSeedHints hints = CollectRenderedLuaSeedHints(*lines);
    const bool has_field_shapes = hints.has_dot || hints.has_index || hints.has_combatstyles;
    bool changed = false;

    if (level != RenderedLuaSeedNormalizationLevel::kHazard && has_field_shapes) {
        changed |= NormalizeRenderedLuaLocalLiteralMissingFieldSeeds(lines);
    }
    if (has_field_shapes) {
        changed |= NormalizeRenderedLuaMissingFieldBaseSeeds(lines);
        changed |= NormalizeRenderedLuaMissingDottedFieldSeeds(lines);
        changed |= NormalizeRenderedLuaTemporaryIndexedFieldSeeds(lines);
        changed |= NormalizeRenderedLuaTemporaryNestedFieldSeeds(lines);
    }
    if (hints.has_combatstyles) {
        changed |= NormalizeRenderedLuaCombatStylesIndexedAliasWrites(lines);
        changed |= NormalizeRenderedLuaCombatStylesNestedFieldSeeds(lines);
    }
    if (hints.has_nil) {
        if (level != RenderedLuaSeedNormalizationLevel::kHazard) {
            changed |= NormalizeRenderedLuaFieldNilTablePlaceholdersBeforeWrites(lines);
        }
        changed |= NormalizeRenderedLuaLiteralNilFieldPlaceholdersBeforeWrites(lines);
    }
    if (hints.has_permanents) {
        changed |= NormalizeRenderedLuaPermanentsTableHelperGuards(lines);
    }
    if (hints.has_special_chest) {
        changed |= NormalizeRenderedLuaSpecialChestGlobalSeed(lines);
    }

    if (level == RenderedLuaSeedNormalizationLevel::kExtended && has_field_shapes) {
        changed |= NormalizeRenderedLuaForwardFieldWritesBeforeLocalTableSeeds(lines);
        changed |= NormalizeRenderedLuaMissingIndexedTableSeeds(lines);
        changed |= NormalizeRenderedLuaCallFieldTableAliasSeeds(lines);
        changed |= refii::lua::NormalizeRenderedLuaNilTablePlaceholders(lines);
    }

    return changed;
}

bool ApplyRenderedLuaNearExactSingletonSeedCluster(
    std::vector<std::string>* lines,
    bool include_subclass_fallback,
    bool include_local_literal_missing_field) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    changed |= NormalizeRenderedLuaNearExactCreateSubClassSeeds(lines);
    if (include_subclass_fallback) {
        changed |= NormalizeRenderedLuaNearExactCreateSubClassSeedsFallback(lines);
    }
    changed |= NormalizeRenderedLuaNearExactSingletonFieldTableAssignments(lines);
    changed |= NormalizeRenderedLuaNearExactSingletonIndexTableAssignments(lines);
    changed |= NormalizeRenderedLuaSeededSingletonTableReplacementChains(lines);
    changed |= NormalizeRenderedLuaHashedKeyLoadTableAssignments(lines);
    changed |= NormalizeRenderedLuaChainedTableElementAssignments(lines);
    if (include_local_literal_missing_field) {
        changed |= NormalizeRenderedLuaLocalLiteralMissingFieldSeeds(lines);
    }
    return changed;
}

bool ApplyRenderedLuaSharedInstructionMicroDiffCore(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    changed |= ApplyRenderedLuaSeedNormalizations(
        lines, RenderedLuaSeedNormalizationLevel::kStandard);
    changed |= ApplyRenderedLuaNearExactSingletonSeedCluster(
        lines, true, false);
    changed |= NormalizeRenderedLuaNearExactTopLevelSingletonFieldSeeds(lines);
    changed |= NormalizeRenderedLuaSparseSingletonTableReplacementChains(lines);
    changed |= refii::lua::NormalizeRenderedLuaNearExactHeadGlobalSeeds(lines);
    changed |= refii::lua::SimplifyRenderedLuaConditionHeaders(lines);
    changed |= refii::lua::SimplifyRenderedLuaRedundantGuardConditionParentheses(lines);
    changed |= refii::lua::SimplifyRenderedLuaEmptyConditionBooleanReturns(lines);
    changed |= refii::lua::SimplifyRenderedLuaConditionalReturns(lines);
    changed |= refii::lua::ExpandRenderedLuaPrepareToRunBooleanReturns(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanLoadPairs(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanCarryConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateAssignedRepeatedConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateAssignedCallGuards(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateAssignedMethodCallGuards(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedCallGuards(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedCallResultGuards(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateLocalCallConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaCallReassignmentSelfCompareGuards(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelNamespaceTableAliasChains(lines);
    changed |= refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries(lines);
    return changed;
}
