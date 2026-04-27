bool SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr std::string_view kStepName =
        "SimplifyRenderedLuaTopLevelTemporaryMultiTableAssignments";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.empty() || trimmed.starts_with("local ")) {
                continue;
            }

            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            std::vector<std::string> lhs_parts;
            std::vector<std::string> rhs_parts;
            if (!SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(0, equal)),
                                                 &lhs_parts) ||
                !SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(equal + 1)),
                                                 &rhs_parts) ||
                lhs_parts.size() != rhs_parts.size() || lhs_parts.size() < 3) {
                continue;
            }

            bool all_temp_lhs = true;
            bool all_table_rhs = true;
            bool has_cross_temp_reference = false;
            std::vector<std::string> normalized_lhs_parts;
            normalized_lhs_parts.reserve(lhs_parts.size());
            for (std::size_t i = 0; i < lhs_parts.size(); ++i) {
                const std::string normalized_lhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[i]), nullptr);
                const std::string normalized_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[i]), nullptr);
                normalized_lhs_parts.push_back(normalized_lhs);
                if (!IsRenderTemporaryName(normalized_lhs)) {
                    all_temp_lhs = false;
                    break;
                }
                if (normalized_rhs.size() < 2 || normalized_rhs.front() != '{' ||
                    normalized_rhs.back() != '}') {
                    all_table_rhs = false;
                    break;
                }
            }
            if (!all_temp_lhs || !all_table_rhs) {
                continue;
            }

            for (std::size_t rhs_index = 0; rhs_index < rhs_parts.size() &&
                                            !has_cross_temp_reference;
                 ++rhs_index) {
                for (const std::string& temp_name : normalized_lhs_parts) {
                    if (CountIdentifierUses(rhs_parts[rhs_index], temp_name) != 0) {
                        has_cross_temp_reference = true;
                        break;
                    }
                }
            }
            if (!has_cross_temp_reference) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();

            std::vector<std::string> replacement_lines;
            replacement_lines.reserve(lhs_parts.size());
            for (std::size_t i = 0; i < lhs_parts.size(); ++i) {
                replacement_lines.push_back(indent + normalized_lhs_parts[i] + " = " +
                                            std::string(TrimLuaWhitespace(rhs_parts[i])));
            }
            replacement_lines.back() += std::string(comment);

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          replacement_lines.begin(),
                          replacement_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr std::string_view kStepName =
        "SimplifyRenderedLuaSetlistCollapsedTopLevelAssignments";
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.empty() || trimmed.starts_with("local ")) {
                continue;
            }

            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            std::vector<std::string> lhs_parts;
            std::vector<std::string> rhs_parts;
            if (!SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(0, equal)),
                                                 &lhs_parts) ||
                !SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(equal + 1)),
                                                 &rhs_parts) ||
                lhs_parts.size() != rhs_parts.size() || lhs_parts.size() < 3) {
                continue;
            }

            bool saw_setlist_banner = false;
            std::size_t banner_begin = line_index;
            std::size_t skipped_seed_lines = 0;
            std::unordered_set<std::string> skipped_seed_temporaries;
            for (std::size_t probe = line_index; probe > 0;) {
                --probe;
                const std::string_view previous_trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (previous_trimmed.empty()) {
                    continue;
                }
                if (!IsRenderedSetlistErrorBanner((*lines)[probe])) {
                    bool seed_is_local = false;
                    std::string seed_lhs;
                    std::string seed_rhs;
                    if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                          &seed_is_local,
                                                          &seed_lhs,
                                                          &seed_rhs)) {
                        const std::string normalized_seed_lhs =
                            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_lhs), nullptr);
                        const std::string normalized_seed_rhs =
                            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
                        const bool seed_is_table_literal =
                            normalized_seed_rhs == "{}" ||
                            (!normalized_seed_rhs.empty() && normalized_seed_rhs.front() == '{' &&
                             normalized_seed_rhs.back() == '}');
                        if (seed_is_local && IsRenderTemporaryName(normalized_seed_lhs) &&
                            seed_is_table_literal && ++skipped_seed_lines <= 8) {
                            skipped_seed_temporaries.insert(normalized_seed_lhs);
                            continue;
                        }
                    }
                    break;
                }
                banner_begin = probe;
                if (IsRenderedSetlistMissingListBanner(previous_trimmed)) {
                    saw_setlist_banner = true;
                }
            }
            if (!saw_setlist_banner) {
                continue;
            }

            bool has_member_target = false;
            bool has_table_rhs = false;
            bool has_skipped_temp_rhs = false;
            bool all_safe = true;
            for (std::size_t i = 0; i < lhs_parts.size(); ++i) {
                lhs_parts[i] = StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[i]), nullptr);
                rhs_parts[i] = TrimLuaWhitespace(rhs_parts[i]);
                if (!IsRenderedSafePrefixExpression(lhs_parts[i])) {
                    all_safe = false;
                    break;
                }
                has_member_target = has_member_target || lhs_parts[i].find_first_of(".[") != std::string::npos;
                const std::string normalized_rhs =
                    StripRenderedWholeParentheses(rhs_parts[i], nullptr);
                has_table_rhs = has_table_rhs ||
                                (!normalized_rhs.empty() && normalized_rhs.front() == '{' &&
                                 normalized_rhs.back() == '}');
                has_skipped_temp_rhs =
                    has_skipped_temp_rhs || skipped_seed_temporaries.contains(normalized_rhs);
            }
            if (!all_safe || !has_member_target || (!has_table_rhs && !has_skipped_temp_rhs)) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();

            std::vector<std::string> replacement_lines;
            replacement_lines.reserve(lhs_parts.size());
            for (std::size_t i = 0; i < lhs_parts.size(); ++i) {
                if (lhs_parts[i].empty() || rhs_parts[i].empty()) {
                    all_safe = false;
                    break;
                }
                replacement_lines.push_back(indent + lhs_parts[i] + " = " + std::string(rhs_parts[i]));
            }
            if (!all_safe || replacement_lines.empty()) {
                continue;
            }

            replacement_lines.back() += std::string(comment);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(banner_begin),
                         lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(banner_begin),
                          replacement_lines.begin(),
                          replacement_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}
