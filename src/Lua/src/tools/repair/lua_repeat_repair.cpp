#include "tools/lua_decompiler_internal.h"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {
namespace {

std::size_t ExtractLineNumberAfterNeedle(std::string_view text, std::string_view needle) {
    const std::size_t start = text.find(needle);
    if (start == std::string_view::npos) {
        return 0;
    }

    std::size_t number_begin = start + needle.size();
    std::size_t number_end = number_begin;
    while (number_end < text.size() &&
           std::isdigit(static_cast<unsigned char>(text[number_end])) != 0) {
        ++number_end;
    }
    if (number_end == number_begin) {
        return 0;
    }

    return static_cast<std::size_t>(
        std::strtoul(std::string(text.substr(number_begin, number_end - number_begin)).c_str(),
                     nullptr,
                     10));
}

bool RewriteRepeatLineToWhileTrue(std::vector<std::string>* lines, std::size_t line_index) {
    if (lines == nullptr || line_index >= lines->size() ||
        TrimWhitespace(StripLineComment((*lines)[line_index])) != "repeat") {
        return false;
    }

    std::string& line = (*lines)[line_index];
    const std::size_t indent_end = line.find_first_not_of(" \t");
    if (indent_end == std::string::npos) {
        line = "while true do";
    } else {
        line.replace(indent_end, std::string::npos, "while true do");
    }
    return true;
}

}  // namespace

bool TryRewriteReportedDanglingRepeatLoop(std::string& decompiled_source,
                                          std::string_view validation_error,
                                          std::string* note) {
    constexpr std::string_view kNeedle = "to close 'repeat' at line ";
    if (validation_error.find("'until' expected") == std::string_view::npos ||
        validation_error.find(kNeedle) == std::string_view::npos) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const std::size_t open_repeat_line = ExtractLineNumberAfterNeedle(validation_error, kNeedle);
    if (reported_line == 0 && open_repeat_line == 0) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if ((reported_line != 0 && reported_line > lines.size()) ||
        (open_repeat_line != 0 && open_repeat_line > lines.size())) {
        return false;
    }

    bool changed = false;
    if (open_repeat_line != 0) {
        changed = RewriteRepeatLineToWhileTrue(&lines, open_repeat_line - 1);
    }
    if (!changed && reported_line != 0) {
        changed = RewriteRepeatLineToWhileTrue(&lines, reported_line - 1);
    }
    if (!changed) {
        constexpr std::size_t kNearbySearchRadius = 3;
        const auto try_search_near = [&](std::size_t one_based_line) {
            if (one_based_line == 0) {
                return false;
            }
            const std::size_t start =
                one_based_line > kNearbySearchRadius ? one_based_line - kNearbySearchRadius - 1 : 0;
            const std::size_t end = std::min(lines.size(), one_based_line + kNearbySearchRadius);
            for (std::size_t line_index = start; line_index < end; ++line_index) {
                if (RewriteRepeatLineToWhileTrue(&lines, line_index)) {
                    return true;
                }
            }
            return false;
        };

        changed = try_search_near(open_repeat_line);
        if (!changed) {
            changed = try_search_near(reported_line);
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_reported_repeat_to_while=open_" +
                              std::to_string(open_repeat_line) + "_line_" +
                              std::to_string(reported_line));
    }
    return true;
}

bool TryRewriteDanglingRepeatLoops(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::vector<std::size_t> repeat_lines;
    std::size_t until_count = 0;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed == "repeat") {
            repeat_lines.push_back(i);
        } else if (trimmed == "until" || StartsWith(trimmed, "until ")) {
            ++until_count;
        }
    }

    if (repeat_lines.empty() || repeat_lines.size() <= until_count) {
        return false;
    }

    const std::size_t unmatched_repeats = repeat_lines.size() - until_count;
    for (std::size_t i = repeat_lines.size() - unmatched_repeats; i < repeat_lines.size(); ++i) {
        RewriteRepeatLineToWhileTrue(&lines, repeat_lines[i]);
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_repeat_to_while");
    }
    return true;
}

bool TryRewriteWhileTrueToRepeat(std::string& decompiled_source,
                                 std::string_view validation_error,
                                 std::string* note) {
    if (validation_error.find("near 'until'") == std::string_view::npos &&
        validation_error.find("'until' expected") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "while true do") {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::size_t indent_end = candidate_lines[i].find_first_not_of(" \t");
        if (indent_end == std::string::npos) {
            candidate_lines[i] = "repeat";
        } else {
            candidate_lines[i].replace(indent_end, std::string::npos, "repeat");
        }

        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_while_true_to_repeat");
            }
            return true;
        }

        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_while_true_to_repeat_progress");
    }
    return true;
}

}  // namespace refii::lua::internal
