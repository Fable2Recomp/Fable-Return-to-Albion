bool DisambiguateRenderedLuaLeadingIifeStatements(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("(function")) {
            continue;
        }

        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string::npos || line[first_non_space] == ';') {
            continue;
        }

        line.insert(first_non_space, 1, ';');
        changed = true;
    }
    return changed;
}

bool NormalizeRenderedLuaWhitespace(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;

    for (std::string& line : *lines) {
        const std::size_t trimmed_size = TrimLuaWhitespace(line).size();
        std::size_t keep_size = line.size();
        while (keep_size > 0 &&
               std::isspace(static_cast<unsigned char>(line[keep_size - 1])) != 0) {
            --keep_size;
        }
        if (keep_size != line.size()) {
            line.erase(keep_size);
            changed = true;
        }
        if (keep_size == 0 && trimmed_size == 0 && !line.empty()) {
            line.clear();
            changed = true;
        }
    }

    std::vector<std::string> normalized;
    normalized.reserve(lines->size());
    bool previous_blank = true;
    for (const std::string& line : *lines) {
        const bool blank = TrimLuaWhitespace(line).empty();
        if (blank && previous_blank) {
            changed = true;
            continue;
        }
        normalized.push_back(line);
        previous_blank = blank;
    }

    while (!normalized.empty() && TrimLuaWhitespace(normalized.back()).empty()) {
        normalized.pop_back();
        changed = true;
    }

    if (normalized.size() != lines->size()) {
        *lines = std::move(normalized);
        return true;
    }

    if (changed) {
        *lines = std::move(normalized);
    }
    return changed;
}

void AppendIndentedStatement(std::vector<std::string>* lines,
                             std::string_view indent,
                             std::string_view statement) {
    std::string normalized_statement(statement);
    if (normalized_statement.find('\n') == std::string::npos) {
        const std::string_view trimmed = TrimLuaWhitespace(normalized_statement);
        if (!trimmed.empty() && trimmed.front() == '(') {
            normalized_statement.insert(0, ";");
        }
    }

    const std::vector<std::string> rendered_lines = SplitRenderedLines(normalized_statement);
    for (const std::string& rendered_line : rendered_lines) {
        lines->push_back(std::string(indent) + rendered_line);
    }
}
