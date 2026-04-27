bool HasRenderedLuaFunctionPlaceholderSignals(const std::vector<std::string>& lines) {
    for (const std::string& line : lines) {
        if (line.find("function()") != std::string::npos ||
            line.find("function ()") != std::string::npos) {
            return true;
        }
    }
    return false;
}

bool ReplaceRenderedFunctionPlaceholderUsesInLine(std::string_view line,
                                                  std::string_view replacement,
                                                  std::string* rewritten_out) {
    if (rewritten_out == nullptr || replacement.empty()) {
        return false;
    }

    const std::string_view stripped = StripLuaLineComment(line);
    const std::string_view trimmed = TrimLuaWhitespace(stripped);
    if (trimmed.empty() || trimmed == "function()" || trimmed == "function ()" ||
        trimmed.find("= function(") != std::string_view::npos ||
        trimmed.find("= function (") != std::string_view::npos) {
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
        std::size_t placeholder_length = 0;
        if (!in_single_quote && !in_double_quote &&
            line.compare(index, 10, "function()") == 0) {
            placeholder_length = 10;
        } else if (!in_single_quote && !in_double_quote &&
                   line.compare(index, 11, "function ()") == 0) {
            placeholder_length = 11;
        }
        if (placeholder_length != 0) {
            rewritten.append(replacement);
            index += placeholder_length;
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
}

bool ReplaceRenderedFunctionPlaceholderUsesInLineWithReplacements(
    std::string_view line,
    const std::vector<std::string>& replacements,
    std::string* rewritten_out) {
    if (rewritten_out == nullptr || replacements.empty()) {
        return false;
    }

    const std::string_view stripped = StripLuaLineComment(line);
    const std::string_view trimmed = TrimLuaWhitespace(stripped);
    if (trimmed.empty() || trimmed == "function()" || trimmed == "function ()" ||
        trimmed.find("= function(") != std::string_view::npos ||
        trimmed.find("= function (") != std::string_view::npos) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    bool escaped = false;
    bool changed = false;
    std::size_t replacement_index = 0;
    std::string rewritten;
    rewritten.reserve(line.size() + replacements.front().size() * replacements.size());
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

        std::size_t placeholder_length = 0;
        if (!in_single_quote && !in_double_quote &&
            line.compare(index, 10, "function()") == 0) {
            placeholder_length = 10;
        } else if (!in_single_quote && !in_double_quote &&
                   line.compare(index, 11, "function ()") == 0) {
            placeholder_length = 11;
        }

        if (placeholder_length != 0) {
            const std::string& replacement =
                replacements[std::min(replacement_index, replacements.size() - 1)];
            rewritten.append(replacement);
            index += placeholder_length;
            ++replacement_index;
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
}

bool ReplaceRenderedIdentifierNonCallUses(std::string_view line,
                                          std::string_view identifier,
                                          std::string_view replacement,
                                          std::string* rewritten_out) {
    if (rewritten_out == nullptr || identifier.empty()) {
        return false;
    }

    std::string rewritten;
    rewritten.reserve(line.size() + replacement.size());
    bool changed = false;
    std::size_t offset = 0;
    while (true) {
        const std::size_t found = FindIdentifierUse(line, identifier, offset);
        if (found == std::string_view::npos) {
            rewritten.append(line.substr(offset));
            break;
        }

        std::size_t after = found + identifier.size();
        while (after < line.size() &&
               std::isspace(static_cast<unsigned char>(line[after])) != 0) {
            ++after;
        }
        if (after < line.size() && line[after] == '(') {
            rewritten.append(line.substr(offset, after - offset));
            offset = after;
            continue;
        }

        rewritten.append(line.substr(offset, found - offset));
        rewritten.append(replacement);
        offset = found + identifier.size();
        changed = true;
    }

    if (!changed) {
        return false;
    }

    *rewritten_out = std::move(rewritten);
    return true;
}

bool SimplifyRenderedLuaDanglingEmptyFunctionPlaceholders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    if (!HasRenderedLuaFunctionPlaceholderSignals(*lines)) {
        return false;
    }

    bool changed = false;
    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!(trimmed.starts_with("function()(") || trimmed.starts_with("function ()("))) {
            continue;
        }

        const auto try_rewrite = [&](bool require_same_scope) -> bool {
            const std::size_t search_begin = line_index > 16 ? line_index - 16 : 0;
            for (std::size_t probe = line_index; probe-- > search_begin;) {
                if (require_same_scope &&
                    (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index])) {
                    continue;
                }

                const std::string_view probe_trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (!probe_trimmed.starts_with("local ")) {
                    continue;
                }

                const std::size_t equal = probe_trimmed.find('=');
                if (equal == std::string_view::npos) {
                    continue;
                }

                const std::string assign_lhs = std::string(
                    TrimLuaWhitespace(probe_trimmed.substr(6, equal - 6)));
                const std::string_view assign_rhs =
                    TrimLuaWhitespace(probe_trimmed.substr(equal + 1));
                if (!IsValidLuaIdentifier(assign_lhs) ||
                    (assign_rhs != "function()" && assign_rhs != "function ()")) {
                    continue;
                }

                std::size_t function_end = probe + 1;
                while (function_end < lines->size() &&
                       TrimLuaWhitespace(StripLuaLineComment((*lines)[function_end])).empty()) {
                    ++function_end;
                }
                if (function_end >= lines->size() ||
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[function_end])) != "end") {
                    continue;
                }

                const std::size_t placeholder_pos = (*lines)[line_index].find("function()");
                const std::size_t spaced_placeholder_pos =
                    (*lines)[line_index].find("function ()");
                if (placeholder_pos != std::string::npos) {
                    (*lines)[line_index].replace(placeholder_pos, 10, assign_lhs);
                    changed = true;
                    return true;
                }
                if (spaced_placeholder_pos != std::string::npos) {
                    (*lines)[line_index].replace(spaced_placeholder_pos, 11, assign_lhs);
                    changed = true;
                    return true;
                }
            }
            return false;
        };

        if (try_rewrite(true)) {
            continue;
        }
        static_cast<void>(try_rewrite(false));
    }

    return changed;
}

bool SimplifyRenderedLuaDanglingIoOpenReadClose(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::size_t close_pos = trimmed.find(":close()");
        if (close_pos == std::string_view::npos) {
            continue;
        }

        const std::string_view close_target =
            TrimLuaWhitespace(trimmed.substr(0, close_pos));
        if (!IsValidLuaIdentifier(close_target)) {
            continue;
        }

        const std::size_t search_begin = line_index > 6 ? line_index - 6 : 0;
        bool already_declared = false;
        for (std::size_t probe = search_begin; probe < line_index; ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.starts_with("local ")) {
                const std::size_t equal = probe_trimmed.find('=');
                const std::size_t end =
                    equal == std::string_view::npos ? probe_trimmed.size() : equal;
                const std::string_view lhs =
                    TrimLuaWhitespace(probe_trimmed.substr(6, end - 6));
                if (lhs == close_target) {
                    already_declared = true;
                    break;
                }
            }
        }
        if (already_declared) {
            continue;
        }

        for (std::size_t probe = line_index; probe-- > search_begin;) {
            std::string_view stripped = StripLuaLineComment((*lines)[probe]);
            if (stripped.find("io.open(") == std::string_view::npos ||
                stripped.find(":read(") == std::string_view::npos) {
                continue;
            }
            const std::size_t io_pos = stripped.find("io.open(");
            const std::size_t read_pos = stripped.find(":read(", io_pos);
            if (io_pos == std::string_view::npos || read_pos == std::string_view::npos ||
                read_pos <= io_pos) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[probe]));
            const std::string open_expr = std::string(
                TrimLuaWhitespace(stripped.substr(io_pos, read_pos - io_pos)));
            if (open_expr.empty()) {
                continue;
            }

            std::string rewritten_line = (*lines)[probe];
            const std::string io_read_expr =
                std::string(stripped.substr(io_pos, read_pos - io_pos)) + ":read(";
            const std::size_t io_read_pos = rewritten_line.find(io_read_expr);
            if (io_read_pos == std::string::npos) {
                continue;
            }

            rewritten_line.replace(io_read_pos,
                                   io_read_expr.size(),
                                   std::string(close_target) + ":read(");
            (*lines)[probe] = std::move(rewritten_line);

            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(probe),
                          indent + "local " + std::string(close_target) + " = " + open_expr);
            changed = true;
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaDanglingLocalFunctionAliases(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    std::string last_global_callee;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));

        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        std::string callee;
        std::vector<std::string> arguments;
        if (ParseRenderedAssignmentLineAllowFunctionPlaceholders((*lines)[line_index],
                                                                  &assign_is_local,
                                                                  &lhs_names,
                                                                  &rhs_expression)) {
            if (ParseRenderedSingleCallLine(rhs_expression, &callee, &arguments)) {
                const std::string normalized_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(callee), nullptr);
                if (normalized_rhs.find('.') != std::string::npos &&
                    IsRenderedSafePrefixExpression(normalized_rhs)) {
                    last_global_callee = normalized_rhs;
                }

                if (!last_global_callee.empty() && IsRenderTemporaryName(normalized_rhs)) {
                    const std::size_t callee_pos = (*lines)[line_index].find(callee);
                    if (callee_pos != std::string::npos) {
                        (*lines)[line_index].replace(callee_pos,
                                                     callee.size(),
                                                     last_global_callee);
                        changed = true;
                    }
                }
            }
        } else if (ParseRenderedSingleCallLine(trimmed, &callee, &arguments)) {
            const std::string normalized =
                StripRenderedWholeParentheses(TrimLuaWhitespace(callee), nullptr);
            if (normalized.find('.') != std::string::npos &&
                IsRenderedSafePrefixExpression(normalized)) {
                last_global_callee = normalized;
            }
        }
    }

    return changed;
}
