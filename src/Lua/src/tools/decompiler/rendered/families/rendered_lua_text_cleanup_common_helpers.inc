namespace {

bool IsRenderedSetlistErrorBanner(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(line);
    return trimmed == "-- DECOMPILER ERROR: No list found. Setlist fails" ||
           trimmed == "-- DECOMPILER ERROR: Overwrote pending register.";
}

bool IsRenderedSetlistMissingListBanner(std::string_view line) {
    return TrimLuaWhitespace(line) == "-- DECOMPILER ERROR: No list found. Setlist fails";
}

void CollectRenderedTemporaryNames(std::string_view expression,
                                   std::unordered_set<std::string>* names) {
    if (names == nullptr) {
        return;
    }
    for (std::size_t index = 0; index < expression.size();) {
        const unsigned char ch = static_cast<unsigned char>(expression[index]);
        if ((std::isalpha(ch) == 0) && expression[index] != '_') {
            ++index;
            continue;
        }
        const std::size_t start = index;
        ++index;
        while (index < expression.size()) {
            const unsigned char next = static_cast<unsigned char>(expression[index]);
            if ((std::isalnum(next) == 0) && expression[index] != '_') {
                break;
            }
            ++index;
        }
        const std::string_view token = expression.substr(start, index - start);
        if (IsRenderTemporaryName(token)) {
            names->insert(std::string(token));
        }
    }
}

bool HasRenderedTemporaryAssignmentInRange(const std::vector<std::string>& lines,
                                           std::size_t begin,
                                           std::size_t end,
                                           std::string_view temporary_name) {
    if (begin >= lines.size() || temporary_name.empty()) {
        return false;
    }
    end = std::min(end, lines.size());
    for (std::size_t probe = begin; probe < end; ++probe) {
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        bool assign_is_local = false;
        if (!ParseRenderedAssignmentLine(lines[probe],
                                         &assign_is_local,
                                         &lhs_names,
                                         &rhs_expression)) {
            continue;
        }
        for (const std::string& lhs_name : lhs_names) {
            if (TrimLuaWhitespace(lhs_name) == temporary_name) {
                return true;
            }
        }
    }
    return false;
}

int ComputeRenderedBraceDeltaIgnoringStringsAndComments(std::string_view line) {
    int delta = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t index = 0; index < line.size(); ++index) {
        if (!in_single_quote && !in_double_quote && index + 1 < line.size() &&
            line[index] == '-' && line[index + 1] == '-') {
            break;
        }

        if (in_single_quote) {
            if (line[index] == '\\' && index + 1 < line.size()) {
                ++index;
            } else if (line[index] == '\'') {
                in_single_quote = false;
            }
            continue;
        }

        if (in_double_quote) {
            if (line[index] == '\\' && index + 1 < line.size()) {
                ++index;
            } else if (line[index] == '"') {
                in_double_quote = false;
            }
            continue;
        }

        if (line[index] == '\'') {
            in_single_quote = true;
            continue;
        }
        if (line[index] == '"') {
            in_double_quote = true;
            continue;
        }
        if (line[index] == '{') {
            ++delta;
        } else if (line[index] == '}') {
            --delta;
        }
    }
    return delta;
}

bool IsRenderedScopePathPrefix(const std::vector<std::size_t>& prefix,
                               const std::vector<std::size_t>& candidate) {
    return prefix.size() <= candidate.size() &&
           std::equal(prefix.begin(), prefix.end(), candidate.begin());
}

} // namespace
