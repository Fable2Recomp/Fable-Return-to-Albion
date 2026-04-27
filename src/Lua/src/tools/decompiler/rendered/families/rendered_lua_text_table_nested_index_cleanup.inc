bool SimplifyRenderedLuaBalverineSequenceSetlistArtifacts(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto normalize = [](std::string_view expression) {
        return StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
    };
    const auto parse_string_literal = [&](std::string_view expression, std::string* value) {
        const std::string normalized = normalize(expression);
        if (normalized.size() < 2) {
            return false;
        }
        const char quote = normalized.front();
        if ((quote != '"' && quote != '\'') || normalized.back() != quote) {
            return false;
        }
        if (value != nullptr) {
            *value = normalized.substr(1, normalized.size() - 2);
        }
        return true;
    };
    const auto build_literal = [](const std::vector<std::string>& entries) {
        std::string literal = "{";
        for (std::size_t i = 0; i < entries.size(); ++i) {
            if (i != 0) {
                literal += ", ";
            }
            literal += entries[i];
        }
        literal += "}";
        return literal;
    };

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaBalverineSequenceSetlistArtifacts";
    RunRenderedLuaCleanupPassLoop(kStepName, 8, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression) ||
                is_local) {
                continue;
            }

            const std::string list_target = normalize(lhs_expression);
            const std::string list_rhs = normalize(rhs_expression);
            if (!IsRenderTemporaryName(list_target) || list_rhs.size() < 2 ||
                list_rhs.front() != '{' || list_rhs.back() != '}') {
                continue;
            }

            std::vector<std::string> entries;
            if (!SplitRenderedTopLevelCommaChain(
                    TrimLuaWhitespace(list_rhs.substr(1, list_rhs.size() - 2)), &entries) ||
                entries.size() != 10) {
                continue;
            }

            if (normalize(entries[6]) != "3" || normalize(entries[7]) != "3" ||
                normalize(entries[8]) != "\"BalverineRearAttack\"" ||
                normalize(entries[9]) != "\"BackOff\"") {
                continue;
            }

            std::unordered_map<std::string, std::string> string_temps;
            std::size_t block_begin = line_index;
            std::size_t scan_begin = line_index;
            while (scan_begin > 0) {
                const std::size_t probe = scan_begin - 1;
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    break;
                }
                const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (trimmed.empty() || IsRenderedSetlistErrorBanner((*lines)[probe])) {
                    scan_begin = probe;
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                       &probe_is_local,
                                                       &probe_lhs,
                                                       &probe_rhs) ||
                    !probe_is_local) {
                    break;
                }

                const std::string normalized_probe_lhs = normalize(probe_lhs);
                if (!IsRenderTemporaryName(normalized_probe_lhs)) {
                    break;
                }

                std::string rhs_value;
                if (parse_string_literal(probe_rhs, &rhs_value)) {
                    string_temps.emplace(normalized_probe_lhs, std::move(rhs_value));
                }
                scan_begin = probe;
            }
            block_begin = scan_begin;

            const auto lookup_string = [&](std::string_view expression, std::string* value) {
                const std::string normalized = normalize(expression);
                const auto it = string_temps.find(normalized);
                if (it == string_temps.end()) {
                    return false;
                }
                if (value != nullptr) {
                    *value = it->second;
                }
                return true;
            };

            std::string hero_value;
            std::string shot_value;
            std::string magic_value;
            std::string combo_value;
            if (!lookup_string(entries[0], &hero_value) ||
                !lookup_string(entries[1], &shot_value) ||
                !lookup_string(entries[2], &magic_value) ||
                !lookup_string(entries[4], &combo_value)) {
                continue;
            }

            static constexpr std::string_view kHeroSuffix = "HeroFlourishResponse";
            static constexpr std::string_view kShotSuffix = "ShotCausedClaw";
            static constexpr std::string_view kMagicSuffix = "MagicedCausedClaw";
            static constexpr std::string_view kComboSuffix = "Combo";
            if (!hero_value.ends_with(kHeroSuffix) || !shot_value.ends_with(kShotSuffix) ||
                !magic_value.ends_with(kMagicSuffix) || !combo_value.ends_with(kComboSuffix)) {
                continue;
            }

            const std::string family_prefix =
                hero_value.substr(0, hero_value.size() - kHeroSuffix.size());
            if (shot_value != family_prefix + std::string(kShotSuffix) ||
                magic_value != family_prefix + std::string(kMagicSuffix) ||
                combo_value != family_prefix + std::string(kComboSuffix)) {
                continue;
            }

            std::vector<std::string> repaired_entries = {
                "\"" + hero_value + "\"",
                "\"" + shot_value + "\"",
                "\"" + magic_value + "\"",
                "{\"" + family_prefix + "Frenzy\", 4}",
                "{\"" + family_prefix + "FrenzyLowHealth\", 3}",
                "\"" + family_prefix + "OrbSuckingResponse\"",
                "\"" + family_prefix + "PostJumpCombo\"",
                "\"" + family_prefix + "CloseCombo\"",
                "\"" + combo_value + "\"",
                "{\"IdleStrafe\", 3}",
                "{\"IdleStrafeLeft\", 3}",
                "{\"IdleStrafeRight\", 3}",
                "\"BalverineLeftAttack\"",
                "\"BalverineRightAttack\"",
                "\"BalverineRearAttack\"",
                "\"BackOff\"",
            };
            const std::string repaired_literal = build_literal(repaired_entries);

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();

            (*lines)[line_index] =
                indent + list_target + " = " + repaired_literal + std::string(comment);
            if (block_begin < line_index) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(block_begin),
                             lines->begin() + static_cast<std::ptrdiff_t>(line_index));
                line_index = block_begin;
            }

            const std::string melee_prefix = "[CombatSituations.Melee] = " + list_rhs;
            const std::string melee_replacement =
                "[CombatSituations.Melee] = " + list_target;
            for (std::size_t use_line = line_index + 1;
                 use_line < lines->size() && use_line < line_index + 12;
                 ++use_line) {
                if (use_line >= scope_paths.size() || !scope_paths[use_line].empty()) {
                    continue;
                }
                std::size_t match = (*lines)[use_line].find(melee_prefix);
                if (match == std::string::npos) {
                    continue;
                }
                (*lines)[use_line].replace(match,
                                           melee_prefix.size(),
                                           melee_replacement);
                break;
            }

            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaNestedLiteralIndexAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto split_relative_nested_target = [](std::string_view lhs,
                                                 std::string* base_key,
                                                 std::string* nested_suffix) {
        lhs = TrimLuaWhitespace(lhs);
        if (lhs.empty() || lhs.front() != '[') {
            return false;
        }

        bool in_single_quote = false;
        bool in_double_quote = false;
        int bracket_depth = 0;
        std::size_t first_close = std::string_view::npos;
        for (std::size_t index = 0; index < lhs.size(); ++index) {
            const char ch = lhs[index];
            if (in_single_quote) {
                if (ch == '\\' && index + 1 < lhs.size()) {
                    ++index;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }
            if (in_double_quote) {
                if (ch == '\\' && index + 1 < lhs.size()) {
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
            if (ch == '[') {
                ++bracket_depth;
                continue;
            }
            if (ch == ']') {
                --bracket_depth;
                if (bracket_depth == 0) {
                    first_close = index;
                    break;
                }
            }
        }

        if (first_close == std::string_view::npos || first_close + 1 >= lhs.size()) {
            return false;
        }

        const std::string_view base = lhs.substr(0, first_close + 1);
        const std::string_view suffix = TrimLuaWhitespace(lhs.substr(first_close + 1));
        if (suffix.empty() || (suffix.front() != '[' && suffix.front() != '.')) {
            return false;
        }

        if (base_key != nullptr) {
            *base_key = std::string(base);
        }
        if (nested_suffix != nullptr) {
            *nested_suffix = std::string(suffix);
        }
        return true;
    };

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaNestedLiteralIndexAssignments";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                    &indent,
                                                    &lhs_prefix,
                                                    &table_name)) {
                continue;
            }

            const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, line_index);
            if (table_end >= lines->size() || table_end <= line_index) {
                continue;
            }

            struct ExtractedBlock {
                std::size_t start_line = 0;
                std::size_t end_line = 0;
                std::vector<std::string> emitted_lines;
            };

            std::unordered_set<std::string> top_level_keys;
            std::vector<ExtractedBlock> extracted_blocks;
            std::size_t implicit_table_entry_index = 0;

            int brace_depth = 1;
            for (std::size_t body_line = line_index + 1; body_line < table_end; ++body_line) {
                const int depth_before_line = brace_depth;
                const std::string_view stripped = StripLuaLineComment((*lines)[body_line]);
                std::string_view trimmed = TrimLuaWhitespace(stripped);

                if (depth_before_line == 1 && !trimmed.empty() && trimmed != "}" &&
                    trimmed != "}," && trimmed != "end" && trimmed != "end,") {
                    const std::size_t equal = trimmed.find('=');
                    const std::string normalized_lhs =
                        equal == std::string_view::npos
                            ? std::string()
                            : StripRenderedWholeParentheses(
                                  TrimLuaWhitespace(trimmed.substr(0, equal)), nullptr);
                    const std::string normalized_rhs =
                        equal == std::string_view::npos
                            ? std::string()
                            : std::string(TrimLuaWhitespace(trimmed.substr(equal + 1)));

                    if (normalized_lhs.empty() && !trimmed.empty() && trimmed.front() == '{') {
                        ++implicit_table_entry_index;
                        top_level_keys.insert("[" + std::to_string(implicit_table_entry_index) +
                                              "]");
                    }

                    if (!normalized_lhs.empty() && normalized_lhs.front() == '[' &&
                        normalized_lhs.find("][") == std::string::npos &&
                        normalized_lhs.find("].") == std::string::npos &&
                        normalized_rhs == "{") {
                        top_level_keys.insert(normalized_lhs);
                    }

                    std::string base_key;
                    std::string nested_suffix;
                    if (!normalized_lhs.empty() &&
                        split_relative_nested_target(
                            normalized_lhs, &base_key, &nested_suffix) &&
                        top_level_keys.contains(base_key) && normalized_rhs == "{") {
                        std::size_t nested_end = body_line;
                        int nested_balance = ComputeRenderedBraceDeltaIgnoringStringsAndComments(
                            (*lines)[body_line]);
                        while (nested_end + 1 < table_end && nested_balance > 0) {
                            ++nested_end;
                            nested_balance +=
                                ComputeRenderedBraceDeltaIgnoringStringsAndComments(
                                    (*lines)[nested_end]);
                        }
                        if (nested_balance != 0) {
                            continue;
                        }

                        ExtractedBlock block;
                        block.start_line = body_line;
                        block.end_line = nested_end;
                        block.emitted_lines.reserve(nested_end - body_line + 1);
                        block.emitted_lines.push_back(indent + table_name + normalized_lhs +
                                                      " = {");
                        for (std::size_t nested_line = body_line + 1; nested_line <= nested_end;
                             ++nested_line) {
                            std::string emitted = (*lines)[nested_line];
                            if (nested_line == nested_end) {
                                std::string_view nested_trimmed =
                                    TrimLuaWhitespace(StripLuaLineComment(emitted));
                                if (nested_trimmed == "}," || nested_trimmed == "}") {
                                    const std::size_t comment_pos = emitted.find("--");
                                    std::string code =
                                        comment_pos == std::string::npos
                                            ? emitted
                                            : emitted.substr(0, comment_pos);
                                    std::string comment =
                                        comment_pos == std::string::npos
                                            ? std::string()
                                            : emitted.substr(comment_pos);
                                    const std::size_t comma = code.rfind(',');
                                    if (comma != std::string::npos) {
                                        code.erase(comma, 1);
                                    }
                                    emitted = code + comment;
                                }
                            }
                            block.emitted_lines.push_back(std::move(emitted));
                        }
                        extracted_blocks.push_back(std::move(block));
                        body_line = nested_end;
                        brace_depth += ComputeRenderedBraceDeltaIgnoringStringsAndComments(
                            (*lines)[nested_end]);
                        continue;
                    }

                    if (!normalized_lhs.empty() && normalized_lhs.front() == '[') {
                        if (split_relative_nested_target(
                                normalized_lhs, &base_key, &nested_suffix)) {
                            if (top_level_keys.contains(base_key)) {
                                std::string emitted_rhs = normalized_rhs;
                                if (!emitted_rhs.empty() && emitted_rhs.back() == ',') {
                                    emitted_rhs.pop_back();
                                }
                                emitted_rhs =
                                    std::string(TrimLuaWhitespace(emitted_rhs));
                                const std::string_view comment =
                                    stripped.size() < (*lines)[body_line].size()
                                        ? std::string_view((*lines)[body_line]).substr(
                                              stripped.size())
                                        : std::string_view();
                                ExtractedBlock block;
                                block.start_line = body_line;
                                block.end_line = body_line;
                                block.emitted_lines.push_back(
                                    indent + table_name + normalized_lhs + " = " + emitted_rhs +
                                    std::string(comment));
                                extracted_blocks.push_back(std::move(block));
                            }
                        } else if (normalized_lhs.find("][") == std::string::npos &&
                                   normalized_lhs.find("].") == std::string::npos) {
                            top_level_keys.insert(normalized_lhs);
                        }
                    }
                }

                brace_depth += ComputeRenderedBraceDeltaIgnoringStringsAndComments(
                    (*lines)[body_line]);
            }

            if (extracted_blocks.empty()) {
                continue;
            }

            std::size_t removed_line_count = 0;
            for (auto block_it = extracted_blocks.rbegin(); block_it != extracted_blocks.rend();
                 ++block_it) {
                removed_line_count += block_it->end_line - block_it->start_line + 1;
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(block_it->start_line),
                             lines->begin() + static_cast<std::ptrdiff_t>(block_it->end_line + 1));
            }

            std::vector<std::string> emitted_lines;
            for (const ExtractedBlock& block : extracted_blocks) {
                emitted_lines.insert(emitted_lines.end(),
                                     block.emitted_lines.begin(),
                                     block.emitted_lines.end());
            }

            const std::size_t adjusted_end = table_end - removed_line_count;
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(adjusted_end + 1),
                          emitted_lines.begin(),
                          emitted_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}
