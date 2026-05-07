#include "tools/decompiler/syntax/lua_syntax_generator_repairs.h"

#include "tools/decompiler/lua_decompiler_internal.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

bool TryRewriteForGeneratorElseArtifacts(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!StartsWith(trimmed, "for ") || trimmed.find("(for generator)") == std::string_view::npos ||
            !EndsWith(trimmed, " do")) {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size() || TrimWhitespace(lines[next]) != "end") {
            continue;
        }

        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        lines[i] = indent + "else";
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(next));
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_for_generator_else");
    }
    return true;
}

bool TryCommentForGeneratorArtifacts(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        const std::string_view trimmed = TrimWhitespace(line);
        if (trimmed.find("(for generator)") == std::string_view::npos) {
            continue;
        }
        if (StartsWith(trimmed, "--")) {
            continue;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
        line = indent + "-- refii-invalid: " + std::string(trimmed);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_for_generator_artifact");
    }
    return true;
}

bool LooksUnsafeToCommentForSyntaxRepair(std::string_view line) {
    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    if (trimmed.empty() || StartsWith(trimmed, "--")) {
        return true;
    }

    int paren_balance = 0;
    int brace_balance = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = 0; i < trimmed.size(); ++i) {
        const char ch = trimmed[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < trimmed.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < trimmed.size()) {
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
            ++paren_balance;
        } else if (ch == ')') {
            --paren_balance;
        } else if (ch == '{') {
            ++brace_balance;
        } else if (ch == '}') {
            --brace_balance;
        }
    }

    if (paren_balance > 0 || brace_balance > 0) {
        return true;
    }

    if (trimmed.find("function(") != std::string_view::npos ||
        trimmed.find("= function(") != std::string_view::npos) {
        return true;
    }

    if (EndsWith(trimmed, ",") || EndsWith(trimmed, "{") || EndsWith(trimmed, "(")) {
        return true;
    }

    if (trimmed == "else" || trimmed == "elseif" || trimmed == "end" || trimmed == "repeat") {
        return true;
    }

    return false;
}

bool TryCommentReportedSyntaxLine(std::string& decompiled_source,
                                  std::string_view validation_error,
                                  std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    std::size_t line_index = 0;
    if (reported_line == 0) {
        line_index = lines.size() - 1;
    } else if (reported_line > lines.size()) {
        line_index = lines.size() - 1;
    } else {
        line_index = reported_line - 1;
    }

    std::size_t candidate = lines.size();
    for (std::size_t i = line_index + 1; i-- > 0;) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!trimmed.empty() && !StartsWith(trimmed, "--") &&
            !LooksUnsafeToCommentForSyntaxRepair(lines[i])) {
            candidate = i;
            break;
        }
        if (i == 0) {
            break;
        }
    }

    if (candidate == lines.size()) {
        for (std::size_t i = line_index + 1; i < lines.size(); ++i) {
            const std::string_view trimmed = TrimWhitespace(lines[i]);
            if (!trimmed.empty() && !StartsWith(trimmed, "--") &&
                !LooksUnsafeToCommentForSyntaxRepair(lines[i])) {
                candidate = i;
                break;
            }
        }
    }

    if (candidate == lines.size()) {
        return false;
    }

    std::string& line = lines[candidate];
    const std::string_view trimmed = TrimWhitespace(line);
    const std::size_t indent_end = line.find_first_not_of(" \t");
    const std::string indent =
        indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
    line = indent + "-- refii-invalid: " + std::string(trimmed);

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_by_commenting_invalid_lines");
    }
    return true;
}

}  // namespace refii::lua::internal
