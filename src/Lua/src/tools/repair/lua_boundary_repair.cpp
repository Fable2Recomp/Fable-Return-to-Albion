#include "tools/lua_decompiler_internal.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdlib>
#include <limits>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {
namespace {

struct LuaBlockBalance {
    int end_deficit = 0;
    int until_deficit = 0;
};

LuaBlockBalance EstimateLuaBlockBalance(std::string_view source) {
    LuaBlockBalance balance;
    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(source, &had_trailing_newline);

    for (const std::string& raw_line : lines) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(raw_line));
        if (trimmed.empty()) {
            continue;
        }

        if (trimmed == "repeat") {
            ++balance.until_deficit;
        } else if (trimmed == "until" || StartsWith(trimmed, "until ")) {
            balance.until_deficit = std::max(0, balance.until_deficit - 1);
        }

        bool opens_end_block = false;
        if ((StartsWith(trimmed, "if ") || StartsWith(trimmed, "while ") ||
             StartsWith(trimmed, "for ")) &&
            (trimmed.find(" then") != std::string_view::npos || EndsWith(trimmed, " then") ||
             trimmed.find(" do") != std::string_view::npos || EndsWith(trimmed, " do"))) {
            opens_end_block = true;
        } else if (trimmed == "do") {
            opens_end_block = true;
        }

        if (ContainsWord(trimmed, "function")) {
            opens_end_block = true;
        }

        if (opens_end_block) {
            ++balance.end_deficit;
        }

        if (trimmed == "end") {
            balance.end_deficit = std::max(0, balance.end_deficit - 1);
        }
    }

    return balance;
}

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

std::string_view TrimLuaCodeLine(std::string_view line) {
    return TrimWhitespace(StripLineComment(line));
}

std::string_view GetLuaLineIndentView(std::string_view line) {
    const std::size_t first_non_ws = line.find_first_not_of(" \t");
    if (first_non_ws == std::string_view::npos) {
        return line;
    }
    return line.substr(0, first_non_ws);
}

bool IsTopLevelAnonymousFunctionBoundaryLine(std::string_view raw_line) {
    if (raw_line.find_first_not_of(" \t") != 0) {
        return false;
    }

    const std::string_view trimmed =
        TrimWhitespace(StripLineComment(raw_line));
    if (trimmed.empty()) {
        return false;
    }

    if (StartsWith(trimmed, ";(function") || StartsWith(trimmed, "(function") ||
        StartsWith(trimmed, "function(") || StartsWith(trimmed, "function ")) {
        return true;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimWhitespace(trimmed.substr(equal + 1));
    return !lhs.empty() && StartsWith(rhs, "function");
}

std::size_t FindPreviousNonEmptyLuaOrSuppressedEndLine(const std::vector<std::string>& lines,
                                                       std::size_t start) {
    if (start == 0 || start > lines.size()) {
        return lines.size();
    }
    for (std::size_t i = start; i-- > 0;) {
        if (!TrimLuaCodeLine(lines[i]).empty() ||
            TrimWhitespace(lines[i]) == "-- refii-suppressed-control: end") {
            return i;
        }
        if (i == 0) {
            break;
        }
    }
    return lines.size();
}

std::size_t FindPreviousNonBlankLuaLine(const std::vector<std::string>& lines, std::size_t start) {
    if (start == 0 || start > lines.size()) {
        return lines.size();
    }
    for (std::size_t i = start; i-- > 0;) {
        if (!TrimWhitespace(lines[i]).empty()) {
            return i;
        }
        if (i == 0) {
            break;
        }
    }
    return lines.size();
}

bool ParseSuppressedCloseLine(std::string_view line, std::string* replacement) {
    constexpr std::string_view kPrefix = "-- refii-suppressed-control:";

    const std::string_view trimmed = TrimWhitespace(line);
    if (!StartsWith(trimmed, kPrefix) || replacement == nullptr) {
        return false;
    }

    const std::string_view payload = TrimWhitespace(trimmed.substr(kPrefix.size()));
    if (!(payload == "end" || payload == "until" || StartsWith(payload, "until "))) {
        return false;
    }

    *replacement = std::string(GetLuaLineIndentView(line)) + std::string(payload);
    return true;
}

bool IsPlausibleDanglingDoParent(std::string_view trimmed) {
    return trimmed == "while true do" || trimmed == "do" || trimmed == "else" ||
           StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ") ||
           StartsWith(trimmed, "function ") || StartsWith(trimmed, "local function ") ||
           StartsWith(trimmed, "return function(") || StartsWith(trimmed, "return function ") ||
           trimmed.find("= function(") != std::string_view::npos ||
           trimmed.find("= function ") != std::string_view::npos;
}

bool HasPlausibleDanglingDoOwner(const std::vector<std::string>& lines,
                                 std::size_t do_line,
                                 std::size_t boundary_line) {
    if (do_line >= lines.size()) {
        return false;
    }

    const std::size_t previous_line = FindPreviousNonBlankLuaLine(lines, do_line);
    if (previous_line < lines.size() &&
        IsPlausibleDanglingDoParent(TrimLuaCodeLine(lines[previous_line]))) {
        return true;
    }

    if (boundary_line >= lines.size()) {
        return false;
    }

    const std::string_view boundary_trimmed = TrimWhitespace(lines[boundary_line]);
    if (boundary_trimmed != "else" && !StartsWith(boundary_trimmed, "elseif ")) {
        return false;
    }

    const std::size_t do_indent_size = GetLuaLineIndent(lines[do_line]).size();
    for (std::size_t probe = do_line; probe-- > 0;) {
        const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
        if (trimmed.empty()) {
            if (probe == 0) {
                break;
            }
            continue;
        }

        const std::size_t indent_size = GetLuaLineIndent(lines[probe]).size();
        if (indent_size > do_indent_size) {
            if (probe == 0) {
                break;
            }
            continue;
        }

        if (indent_size == do_indent_size) {
            if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ") ||
                trimmed == "else") {
                return true;
            }
            if (trimmed == "end" || trimmed == "end," || StartsWith(trimmed, "until ")) {
                return false;
            }
            if (probe == 0) {
                break;
            }
            continue;
        }

        return StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ") ||
               trimmed == "else";
    }

    return false;
}

std::size_t ComputeTopLevelIndentSize(const std::vector<std::string>& lines) {
    std::size_t top_level_indent_size = std::numeric_limits<std::size_t>::max();
    for (const std::string& line : lines) {
        const std::string_view trimmed = TrimLuaCodeLine(line);
        if (trimmed.empty()) {
            continue;
        }
        top_level_indent_size = std::min(top_level_indent_size, GetLuaLineIndentView(line).size());
    }
    return top_level_indent_size == std::numeric_limits<std::size_t>::max()
               ? 0
               : top_level_indent_size;
}

std::size_t FindSuppressedEndRun(const std::vector<std::string>& lines,
                                 std::size_t boundary_line,
                                 std::size_t* run_begin,
                                 std::size_t* run_end) {
    if (run_begin != nullptr) {
        *run_begin = lines.size();
    }
    if (run_end != nullptr) {
        *run_end = lines.size();
    }
    if (boundary_line == 0 || boundary_line > lines.size()) {
        return 0;
    }

    std::size_t current = boundary_line;
    while (current > 0) {
        const std::size_t probe = FindPreviousNonEmptyLuaOrSuppressedEndLine(lines, current);
        if (probe >= lines.size() || TrimWhitespace(lines[probe]) != "-- refii-suppressed-control: end") {
            break;
        }
        if (run_end != nullptr && *run_end >= lines.size()) {
            *run_end = probe;
        }
        if (run_begin != nullptr) {
            *run_begin = probe;
        }
        current = probe;
        if (probe == 0) {
            break;
        }
    }

    if (run_begin == nullptr || *run_begin >= lines.size() || run_end == nullptr ||
        *run_end >= lines.size() || *run_begin > *run_end) {
        return 0;
    }
    return *run_end - *run_begin + 1;
}

enum class MissingControlCloseKind {
    kEnd,
    kUntil,
};

struct MissingControlCloseFrame {
    MissingControlCloseKind kind;
    std::string indent;
};

void UpdateMissingCloseControlStack(std::vector<MissingControlCloseKind>* open_controls,
                                    std::string_view line) {
    if (open_controls == nullptr) {
        return;
    }

    const std::string_view trimmed = TrimLuaCodeLine(line);
    if (trimmed.empty() || trimmed == "else" || StartsWith(trimmed, "elseif ")) {
        return;
    }

    const auto pop_last_kind = [&](MissingControlCloseKind kind) {
        for (std::size_t i = open_controls->size(); i-- > 0;) {
            if ((*open_controls)[i] == kind) {
                open_controls->erase(open_controls->begin() + static_cast<std::ptrdiff_t>(i));
                return;
            }
        }
    };

    if (trimmed == "repeat") {
        open_controls->push_back(MissingControlCloseKind::kUntil);
        return;
    }
    if (trimmed == "end" || trimmed == "end,") {
        pop_last_kind(MissingControlCloseKind::kEnd);
        return;
    }
    if (trimmed == "until" || StartsWith(trimmed, "until ")) {
        pop_last_kind(MissingControlCloseKind::kUntil);
        return;
    }

    if (trimmed == "do" || StartsWith(trimmed, "if ") || StartsWith(trimmed, "for ") ||
        StartsWith(trimmed, "while ") || StartsWith(trimmed, "function ") ||
        StartsWith(trimmed, "local function ") || StartsWith(trimmed, "return function(") ||
        StartsWith(trimmed, "return function ") ||
        trimmed.find("= function(") != std::string_view::npos ||
        trimmed.find("= function ") != std::string_view::npos) {
        open_controls->push_back(MissingControlCloseKind::kEnd);
    }
}

void UpdateStructuredMissingCloseControlStack(std::vector<MissingControlCloseFrame>* open_controls,
                                              std::string_view line) {
    if (open_controls == nullptr) {
        return;
    }

    const std::string_view trimmed = TrimLuaCodeLine(line);
    if (trimmed.empty() || trimmed == "else" || StartsWith(trimmed, "elseif ")) {
        return;
    }

    const auto pop_last_kind = [&](MissingControlCloseKind kind) {
        for (std::size_t i = open_controls->size(); i-- > 0;) {
            if ((*open_controls)[i].kind == kind) {
                open_controls->erase(open_controls->begin() + static_cast<std::ptrdiff_t>(i));
                return;
            }
        }
    };

    if (trimmed == "repeat") {
        open_controls->push_back(
            {MissingControlCloseKind::kUntil, std::string(GetLuaLineIndentView(line))});
        return;
    }
    if (trimmed == "end" || trimmed == "end,") {
        pop_last_kind(MissingControlCloseKind::kEnd);
        return;
    }
    if (trimmed == "until" || StartsWith(trimmed, "until ")) {
        pop_last_kind(MissingControlCloseKind::kUntil);
        return;
    }

    if (trimmed == "do" || StartsWith(trimmed, "if ") || StartsWith(trimmed, "for ") ||
        StartsWith(trimmed, "while ") || StartsWith(trimmed, "function ") ||
        StartsWith(trimmed, "local function ") || StartsWith(trimmed, "return function(") ||
        StartsWith(trimmed, "return function ") ||
        trimmed.find("= function(") != std::string_view::npos ||
        trimmed.find("= function ") != std::string_view::npos) {
        open_controls->push_back(
            {MissingControlCloseKind::kEnd, std::string(GetLuaLineIndentView(line))});
    }
}

std::vector<std::string> BuildStackAwareSuppressedCloseRun(const std::vector<std::string>& lines,
                                                           std::size_t run_begin,
                                                           std::size_t close_count,
                                                           std::size_t scan_begin) {
    std::vector<MissingControlCloseKind> open_controls;
    open_controls.reserve(64);

    for (std::size_t i = std::min(scan_begin, lines.size()); i < std::min(run_begin, lines.size());
         ++i) {
        UpdateMissingCloseControlStack(&open_controls, lines[i]);
    }

    std::vector<std::string> replacements;
    replacements.reserve(close_count);
    for (std::size_t offset = 0; offset < close_count && run_begin + offset < lines.size();
         ++offset) {
        const std::string_view line = lines[run_begin + offset];
        const std::string indent(GetLuaLineIndentView(line));
        const bool close_repeat =
            !open_controls.empty() && open_controls.back() == MissingControlCloseKind::kUntil;
        replacements.push_back(indent + (close_repeat ? "until true" : "end"));
        if (!open_controls.empty()) {
            open_controls.pop_back();
        }
    }

    return replacements;
}

std::vector<std::string> BuildStructuredMissingCloseRun(const std::vector<std::string>& lines,
                                                        std::size_t boundary_line,
                                                        std::size_t close_count,
                                                        std::size_t scan_begin) {
    std::vector<MissingControlCloseFrame> open_controls;
    open_controls.reserve(64);

    for (std::size_t i = std::min(scan_begin, lines.size());
         i < std::min(boundary_line, lines.size());
         ++i) {
        UpdateStructuredMissingCloseControlStack(&open_controls, lines[i]);
    }

    std::vector<std::string> replacements;
    replacements.reserve(std::min(close_count, open_controls.size()));
    for (std::size_t offset = 0; offset < close_count && offset < open_controls.size();
         ++offset) {
        const MissingControlCloseFrame& frame =
            open_controls[open_controls.size() - 1 - offset];
        replacements.push_back(frame.indent +
                               (frame.kind == MissingControlCloseKind::kUntil ? "until true"
                                                                              : "end"));
    }
    return replacements;
}

}  // namespace

bool TryAppendMissingEnds(std::string& decompiled_source,
                          std::string_view validation_error,
                          std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos) {
        return false;
    }

    std::string candidate = decompiled_source;
    std::string best_candidate;
    std::string best_error;
    for (int i = 0; i < 32; ++i) {
        candidate.append("\nend");
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_by_appending_end");
            }
            return true;
        }
        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = candidate;
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_by_appending_end_progress");
    }
    return true;
}

bool TryAppendMissingUntil(std::string& decompiled_source,
                           std::string_view validation_error,
                           std::string* note) {
    if (validation_error.find("'until' expected") == std::string_view::npos ||
        validation_error.find("to close 'repeat'") == std::string_view::npos) {
        return false;
    }

    std::string candidate = decompiled_source;
    std::string best_candidate;
    std::string best_error;
    for (int i = 0; i < 16; ++i) {
        candidate.append("\nuntil true");
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_by_appending_until");
            }
            return true;
        }
        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = candidate;
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_by_appending_until_progress");
    }
    return true;
}

bool TryBalanceOpenBlocks(std::string& decompiled_source,
                          std::string_view validation_error,
                          std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'until' expected") == std::string_view::npos) {
        return false;
    }

    const LuaBlockBalance balance = EstimateLuaBlockBalance(decompiled_source);
    if (balance.end_deficit <= 0 && balance.until_deficit <= 0) {
        return false;
    }

    const auto append_suffix = [&](bool until_first) {
        std::string suffix;
        for (int i = 0; i < (until_first ? balance.until_deficit : balance.end_deficit); ++i) {
            suffix.append(until_first ? "\nuntil true" : "\nend");
        }
        for (int i = 0; i < (until_first ? balance.end_deficit : balance.until_deficit); ++i) {
            suffix.append(until_first ? "\nend" : "\nuntil true");
        }
        return suffix;
    };

    const std::array<std::string, 2> suffixes = {
        append_suffix(true),
        append_suffix(false),
    };

    std::string best_candidate;
    std::string best_error;
    for (const std::string& suffix : suffixes) {
        if (suffix.empty()) {
            continue;
        }

        std::string candidate = decompiled_source;
        candidate.append(suffix);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_balanced_blocks:end=" +
                                      std::to_string(std::max(0, balance.end_deficit)) +
                                      ",until=" +
                                      std::to_string(std::max(0, balance.until_deficit)));
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
        *note = JoinNotes(*note,
                          "syntax_repaired_balanced_blocks_progress:end=" +
                              std::to_string(std::max(0, balance.end_deficit)) + ",until=" +
                              std::to_string(std::max(0, balance.until_deficit)));
    }
    return true;
}

bool TryMaterializeMissingEndWarnings(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    enum class MissingControlCloseKind {
        kEnd,
        kUntil,
    };

    std::vector<MissingControlCloseKind> open_controls;
    open_controls.reserve(64);

    const auto code_trimmed = [](std::string_view line) {
        return TrimWhitespace(StripLineComment(line));
    };
    const auto pop_last_kind = [&](MissingControlCloseKind kind) {
        for (std::size_t i = open_controls.size(); i-- > 0;) {
            if (open_controls[i] == kind) {
                open_controls.erase(open_controls.begin() + static_cast<std::ptrdiff_t>(i));
                return;
            }
        }
    };
    const auto update_control_stack = [&](std::string_view line) {
        const std::string_view trimmed = code_trimmed(line);
        if (trimmed.empty() || trimmed == "else" || StartsWith(trimmed, "elseif ")) {
            return;
        }

        if (trimmed == "repeat") {
            open_controls.push_back(MissingControlCloseKind::kUntil);
            return;
        }

        if (trimmed == "end" || trimmed == "end,") {
            pop_last_kind(MissingControlCloseKind::kEnd);
            return;
        }

        if (trimmed == "until" || StartsWith(trimmed, "until ")) {
            pop_last_kind(MissingControlCloseKind::kUntil);
            return;
        }

        if (trimmed == "do" || StartsWith(trimmed, "if ") || StartsWith(trimmed, "for ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "function ") ||
            StartsWith(trimmed, "local function ") ||
            StartsWith(trimmed, "return function(") ||
            StartsWith(trimmed, "return function ") ||
            trimmed.find("= function(") != std::string_view::npos ||
            trimmed.find("= function ") != std::string_view::npos) {
            open_controls.push_back(MissingControlCloseKind::kEnd);
        }
    };

    bool changed = false;
    for (std::string& line : lines) {
        if (TrimWhitespace(line) != "-- Warning: missing end command somewhere! Added here") {
            update_control_stack(line);
            continue;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
        const bool close_repeat =
            !open_controls.empty() && open_controls.back() == MissingControlCloseKind::kUntil;
        line = indent + (close_repeat ? "until true" : "end");
        update_control_stack(line);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_materialized_missing_end_warning");
    }
    return true;
}

bool TryMaterializeCommentedControlLines(std::string& decompiled_source, std::string* note) {
    constexpr std::string_view kInvalidPrefix = "-- refii-invalid:";

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        const std::string_view trimmed = TrimWhitespace(line);
        if (!StartsWith(trimmed, kInvalidPrefix)) {
            continue;
        }

        const std::string_view payload = TrimWhitespace(trimmed.substr(kInvalidPrefix.size()));
        if (!(payload == "else" || payload == "end" || payload == "until" ||
              StartsWith(payload, "elseif ") || StartsWith(payload, "until "))) {
            continue;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
        line = indent + std::string(payload);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_materialized_control_lines");
    }
    return true;
}

bool TryCommentDanglingControlLinesAroundInvalidBlocks(std::string& decompiled_source,
                                                       std::string* note) {
    constexpr std::string_view kInvalidPrefix = "-- refii-invalid:";
    constexpr std::string_view kSuppressedControlPrefix = "-- refii-suppressed-control:";

    enum class ControlKind {
        kIfLike,
        kRepeat,
        kOther,
    };

    struct ControlFrame {
        ControlKind kind = ControlKind::kOther;
        std::string indent;
    };

    const auto classify_control_line = [](std::string_view payload,
                                          ControlKind* kind_out,
                                          std::string* indent_out,
                                          std::string_view raw_line) {
        if (kind_out == nullptr || indent_out == nullptr) {
            return false;
        }

        const std::size_t indent_end = raw_line.find_first_not_of(" \t");
        *indent_out = indent_end == std::string_view::npos
                          ? std::string()
                          : std::string(raw_line.substr(0, indent_end));

        payload = TrimWhitespace(payload);
        if (payload.empty()) {
            return false;
        }

        if (payload == "repeat") {
            *kind_out = ControlKind::kRepeat;
            return true;
        }

        if (StartsWith(payload, "if ") || StartsWith(payload, "while ") ||
            StartsWith(payload, "for ") || payload == "do" ||
            StartsWith(payload, "function ") || StartsWith(payload, "local function ") ||
            StartsWith(payload, "return function(") ||
            StartsWith(payload, "return function ") ||
            payload.find("= function(") != std::string_view::npos ||
            payload.find("= function ") != std::string_view::npos) {
            *kind_out = StartsWith(payload, "if ") ? ControlKind::kIfLike : ControlKind::kOther;
            return true;
        }

        return false;
    };

    const auto comment_active_line = [&](std::string* line) {
        if (line == nullptr) {
            return;
        }
        const std::string_view trimmed = TrimWhitespace(*line);
        if (trimmed.empty() || StartsWith(trimmed, "--")) {
            return;
        }

        const std::size_t indent_end = line->find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : line->substr(0, indent_end);
        *line = indent + std::string(kSuppressedControlPrefix) + " " + std::string(trimmed);
    };

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::vector<ControlFrame> active_stack;
    std::vector<ControlFrame> invalid_stack;
    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimWhitespace(lines[line_index]);
        if (trimmed.empty()) {
            continue;
        }

        if (StartsWith(trimmed, kInvalidPrefix)) {
            const std::string_view payload =
                TrimWhitespace(trimmed.substr(kInvalidPrefix.size()));
            ControlKind invalid_kind = ControlKind::kOther;
            std::string invalid_indent;
            if (classify_control_line(payload, &invalid_kind, &invalid_indent, lines[line_index])) {
                invalid_stack.push_back({invalid_kind, std::move(invalid_indent)});
            }
            continue;
        }

        if (StartsWith(trimmed, "--")) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        const std::string_view payload = TrimWhitespace(StripLineComment(lines[line_index]));

        const auto top_active_matches = [&](ControlKind kind) {
            return !active_stack.empty() && active_stack.back().kind == kind &&
                   active_stack.back().indent == indent;
        };
        const auto top_invalid_matches = [&](ControlKind kind) {
            return !invalid_stack.empty() && invalid_stack.back().kind == kind &&
                   invalid_stack.back().indent == indent;
        };

        if (payload == "else" || StartsWith(payload, "elseif ")) {
            if (top_invalid_matches(ControlKind::kIfLike) ||
                !top_active_matches(ControlKind::kIfLike)) {
                comment_active_line(&lines[line_index]);
                changed = true;
            }
            continue;
        }

        if (payload == "end") {
            if (top_invalid_matches(ControlKind::kIfLike) ||
                top_invalid_matches(ControlKind::kOther)) {
                invalid_stack.pop_back();
                comment_active_line(&lines[line_index]);
                changed = true;
                continue;
            }
            if (top_active_matches(ControlKind::kIfLike) ||
                top_active_matches(ControlKind::kOther)) {
                active_stack.pop_back();
                continue;
            }
            comment_active_line(&lines[line_index]);
            changed = true;
            continue;
        }

        if (payload == "until" || StartsWith(payload, "until ")) {
            if (top_invalid_matches(ControlKind::kRepeat)) {
                invalid_stack.pop_back();
                comment_active_line(&lines[line_index]);
                changed = true;
                continue;
            }
            if (top_active_matches(ControlKind::kRepeat)) {
                active_stack.pop_back();
                continue;
            }
            comment_active_line(&lines[line_index]);
            changed = true;
            continue;
        }

        ControlKind active_kind = ControlKind::kOther;
        std::string active_indent;
        if (classify_control_line(payload, &active_kind, &active_indent, lines[line_index])) {
            active_stack.push_back({active_kind, std::move(active_indent)});
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_commented_dangling_control_lines");
    }
    return true;
}

bool TryStripDanglingDoAfterUntil(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!(trimmed == "until" || StartsWith(trimmed, "until "))) {
            continue;
        }

        std::size_t remove_begin = i + 1;
        while (remove_begin < lines.size() && TrimWhitespace(lines[remove_begin]).empty()) {
            ++remove_begin;
        }

        std::size_t remove_end = remove_begin;
        while (remove_end < lines.size() && TrimWhitespace(lines[remove_end]) == "do") {
            ++remove_end;
        }

        if (remove_end == remove_begin) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(remove_begin),
                    lines.begin() + static_cast<std::ptrdiff_t>(remove_end));
        changed = true;
        i = remove_begin > 0 ? remove_begin - 1 : 0;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_dangling_do_after_until");
    }
    return true;
}

bool TryStripDanglingDoBeforeElse(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "do") {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed == "else" || StartsWith(next_trimmed, "elseif ")) {
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
            changed = true;
            --i;
            continue;
        }

        const std::string do_indent = GetLuaLineIndent(lines[i]);
        std::size_t close_line = lines.size();
        std::size_t boundary_line = lines.size();
        std::size_t wrapped_boundary_line = lines.size();
        bool body_is_nested = true;
        bool saw_nested_close = false;
        for (std::size_t probe = next; probe < lines.size() && probe <= i + 96; ++probe) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }

            const std::size_t probe_indent_size = GetLuaLineIndent(lines[probe]).size();
            if ((probe_trimmed == "end" || probe_trimmed == "end," ||
                 StartsWith(probe_trimmed, "until ")) &&
                probe_indent_size > do_indent.size()) {
                saw_nested_close = true;
            }

            if (probe_indent_size == do_indent.size() &&
                (probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif "))) {
                wrapped_boundary_line = probe;
                break;
            }

            if (probe_indent_size <= do_indent.size() &&
                probe_trimmed != "end" && probe_trimmed != "end," &&
                probe_trimmed != "else" && !StartsWith(probe_trimmed, "elseif ")) {
                body_is_nested = false;
                break;
            }

            if (probe_trimmed == "end" || probe_trimmed == "end,") {
                if (probe_indent_size != do_indent.size()) {
                    continue;
                }
                close_line = probe;
                boundary_line = FindNextSignificantLuaLine(lines, probe + 1);
                break;
            }
        }

        if (!body_is_nested) {
            continue;
        }

        bool should_strip = false;
        if (close_line < lines.size() && boundary_line < lines.size()) {
            const std::string_view boundary_trimmed = TrimWhitespace(lines[boundary_line]);
            should_strip =
                (boundary_trimmed == "else" || StartsWith(boundary_trimmed, "elseif ")) &&
                GetLuaLineIndent(lines[boundary_line]).size() < do_indent.size();
        } else if (wrapped_boundary_line < lines.size()) {
            should_strip = saw_nested_close;
        }

        if (!should_strip) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        --i;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_dangling_do");
    }
    return true;
}

bool TryStripDanglingEndBeforeElse(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::size_t next = FindNextSignificantLuaLine(lines, i + 1);
        if (next >= lines.size()) {
            continue;
        }

        bool remove_end = false;
        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed == "return" || StartsWith(next_trimmed, "return ") ||
            next_trimmed == "do return end") {
            const std::size_t next_after_return = FindNextSignificantLuaLine(lines, next + 1);
            if (next_after_return < lines.size()) {
                const std::string_view tail_trimmed = TrimWhitespace(lines[next_after_return]);
                remove_end = tail_trimmed == "else" || StartsWith(tail_trimmed, "elseif ");
            }
        }

        if (!remove_end) {
            std::size_t prev = lines.size();
            for (std::size_t j = i; j-- > 0;) {
                if (!TrimWhitespace(lines[j]).empty()) {
                    prev = j;
                    break;
                }
                if (j == 0) {
                    break;
                }
            }
            if (prev < lines.size()) {
                const std::string_view prev_trimmed = TrimWhitespace(lines[prev]);
                if (StartsWith(prev_trimmed, "-- DECOMPILER ERROR:") ||
                    prev_trimmed == "-- Warning: missing end command somewhere! Added here") {
                    std::size_t probe = next;
                    for (int depth = 0; depth < 2 && probe < lines.size(); ++depth) {
                        const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
                        if (probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif ")) {
                            remove_end = true;
                            break;
                        }
                        probe = FindNextSignificantLuaLine(lines, probe + 1);
                    }
                }
            }
        }

        if (!remove_end) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        --i;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_dangling_end");
    }
    return true;
}

bool TryRemoveExcessEndLines(std::string& decompiled_source,
                             std::string_view validation_error,
                             std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos ||
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t run_end = lines.size(); run_end-- > 0;) {
        if (TrimWhitespace(lines[run_end]) != "end") {
            continue;
        }

        std::size_t run_begin = run_end;
        while (run_begin > 0 && TrimWhitespace(lines[run_begin - 1]) == "end") {
            --run_begin;
        }

        const std::size_t run_length = run_end - run_begin + 1;
        const std::size_t max_remove = std::min<std::size_t>(run_length, 6);
        for (std::size_t remove_count = 1; remove_count <= max_remove; ++remove_count) {
            std::vector<std::string> candidate_lines = lines;
            const std::size_t erase_begin = run_end - remove_count + 1;
            candidate_lines.erase(candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(erase_begin),
                                  candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(run_end + 1));
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note, "syntax_repaired_removed_excess_end");
                }
                return true;
            }

            if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
                (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
                best_candidate = std::move(candidate);
                best_error = candidate_error;
            }
        }

        run_end = run_begin;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_excess_end_progress");
    }
    return true;
}

bool TryStripEmptyElseBlocks(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "else") {
            continue;
        }

        const std::size_t next = FindNextSignificantLuaLine(lines, i + 1);
        if (next >= lines.size() || TrimWhitespace(lines[next]) != "end") {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        --i;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_empty_else");
    }
    return true;
}

bool TryCollapseDuplicatedElseLines(std::string& decompiled_source,
                                    std::string_view validation_error,
                                    std::string* note) {
    if (validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    const auto trim_line = [](std::string_view line) {
        return TrimWhitespace(StripLineComment(line));
    };

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (trim_line(lines[i]) != "else") {
            continue;
        }

        const std::size_t next = FindNextSignificantLuaLine(lines, i + 1);
        if (next >= lines.size() || trim_line(lines[next]) != "else" ||
            GetLuaLineIndent(lines[i]) != GetLuaLineIndent(lines[next])) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_collapsed_duplicated_else");
    }
    return true;
}

bool TryStripStandaloneLocalArtifactLines(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(StripLineComment(lines[i])) != "local") {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_standalone_local");
    }
    return true;
}

bool TryInsertEndBeforeReportedLine(std::string& decompiled_source,
                                    std::string_view validation_error,
                                    std::string* note) {
    const bool missing_end =
        validation_error.find("<eof>") != std::string_view::npos ||
        validation_error.find("'end' expected") != std::string_view::npos;
    const bool malformed_boundary =
        validation_error.find("near 'local'") != std::string_view::npos ||
        validation_error.find("near '('") != std::string_view::npos;
    if (!missing_end && !malformed_boundary) {
        return false;
    }
    if (validation_error.find("near '<eof>'") != std::string_view::npos) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    std::size_t insert_at = reported_line > lines.size() ? lines.size() : reported_line - 1;
    std::size_t probe = insert_at == lines.size() ? lines.size() - 1 : insert_at;
    while (probe > 0) {
        const std::string_view trimmed = TrimWhitespace(lines[probe]);
        if (!trimmed.empty() && !StartsWith(trimmed, "--")) {
            insert_at = probe;
            break;
        }
        --probe;
    }

    const std::string_view reference =
        insert_at < lines.size() ? TrimWhitespace(lines[insert_at]) : std::string_view();
    if (reference == "end" || reference == "else" || StartsWith(reference, "elseif ")) {
        return false;
    }
    if (malformed_boundary && !missing_end && !reference.empty() &&
        !LooksLikeTopLevelBoundary(reference) && !StartsWith(reference, "local ") &&
        !StartsWith(reference, "function(") && !StartsWith(reference, "function ") &&
        !StartsWith(reference, "(")) {
        return false;
    }

    std::string indent;
    if (insert_at < lines.size()) {
        const std::size_t indent_end = lines[insert_at].find_first_not_of(" \t");
        if (indent_end != std::string::npos) {
            indent = lines[insert_at].substr(0, indent_end);
        }
    }

    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);
    std::string best_candidate;
    std::string best_error;
    for (int end_count = 1; end_count <= 8; ++end_count) {
        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(insert_at),
                               static_cast<std::size_t>(end_count),
                               indent + "end");
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
        const bool candidate_pushes_error_later =
            validation_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > validation_error_line;
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_reported_line");
            }
            return true;
        }
        if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
            !candidate_pushes_error_later) {
            continue;
        }

        const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
        const bool candidate_beats_best_by_line =
            best_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > best_error_line;
        if (!best_candidate.empty() &&
            !IsValidationOutcomeBetter(best_error, candidate_error) &&
            !candidate_beats_best_by_line) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "syntax_repaired_inserted_end_before_reported_line_progress");
        }
        return true;
    }
    return false;
}

bool TryInsertEndBeforeElse(std::string& decompiled_source,
                            std::string_view validation_error,
                            std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        (validation_error.find("near 'else'") == std::string_view::npos &&
         validation_error.find("near 'elseif'") == std::string_view::npos)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed != "else" && !StartsWith(trimmed, "elseif ")) {
            continue;
        }

        std::size_t previous = i;
        while (previous > 0) {
            --previous;
            const std::string_view previous_trimmed = TrimWhitespace(lines[previous]);
            if (previous_trimmed.empty()) {
                continue;
            }
            if (previous_trimmed == "end" || previous_trimmed == "else" ||
                StartsWith(previous_trimmed, "elseif ") ||
                StartsWith(previous_trimmed, "--")) {
                previous = lines.size();
            }
            break;
        }
        if (previous == lines.size()) {
            continue;
        }

        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        for (int end_count = 1; end_count <= 4; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                                   static_cast<std::size_t>(end_count), indent + "end");
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
            const bool candidate_pushes_error_later =
                validation_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > validation_error_line;
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_else");
                }
                return true;
            }

            if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
                !candidate_pushes_error_later) {
                continue;
            }

            const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
            const bool candidate_beats_best_by_line =
                best_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > best_error_line;
            if (!best_error.empty() &&
                !IsValidationOutcomeBetter(best_error, candidate_error) &&
                !candidate_beats_best_by_line) {
                continue;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_else_progress");
    }
    return true;
}

bool TryRepairNoLoopToBreakNearElse(std::string& decompiled_source,
                                    std::string_view validation_error,
                                    std::string* note) {
    if (validation_error.find("no loop to break") == std::string_view::npos ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const std::size_t error_index =
        reported_line == 0 ? 0 : std::min(lines.size(), reported_line) - 1;
    const std::size_t search_begin = error_index > 24 ? error_index - 24 : 0;
    const std::size_t search_end = std::min(lines.size(), error_index + 6);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(StripLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t indent_end = line.find_first_not_of(" \t");
        return indent_end == std::string_view::npos ? std::string()
                                                    : std::string(line.substr(0, indent_end));
    };
    const auto find_block_end = [&](std::size_t start) -> std::size_t {
        int depth = 0;
        for (std::size_t probe = start; probe < lines.size(); ++probe) {
            depth += ComputeLuaBlockDelta(lines[probe]);
            if (depth == 0) {
                return probe;
            }
        }
        return lines.size();
    };

    std::string best_candidate;
    std::string best_error;
    for (std::size_t outer_if = search_begin; outer_if < search_end; ++outer_if) {
        std::string outer_condition;
        if (!ParseLuaIfCondition(lines[outer_if], &outer_condition)) {
            continue;
        }

        const std::size_t outer_end = find_block_end(outer_if);
        if (outer_end >= lines.size() || trim_line(lines[outer_end]) != "end") {
            continue;
        }

        const std::string outer_indent = line_indent(lines[outer_if]);
        std::size_t else_line = lines.size();
        for (std::size_t probe = outer_if + 1; probe < outer_end; ++probe) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed == "else" &&
                line_indent(lines[probe]).size() == outer_indent.size()) {
                else_line = probe;
                break;
            }
        }
        if (else_line >= lines.size()) {
            continue;
        }

        const std::size_t else_body_line = FindNextNonEmptyLuaLine(lines, else_line + 1);
        if (else_body_line >= lines.size() || trim_line(lines[else_body_line]) != "break") {
            continue;
        }
        const std::size_t after_break = FindNextNonEmptyLuaLine(lines, else_body_line + 1);
        if (after_break != outer_end) {
            continue;
        }

        const std::size_t inner_end = FindPreviousNonEmptyLuaLine(lines, else_line);
        if (inner_end >= lines.size() || trim_line(lines[inner_end]) != "end" ||
            line_indent(lines[inner_end]).size() <= outer_indent.size()) {
            continue;
        }

        std::size_t inner_if = lines.size();
        for (std::size_t probe = inner_end; probe-- > outer_if + 1;) {
            std::string inner_condition;
            if (!ParseLuaIfCondition(lines[probe], &inner_condition)) {
                continue;
            }
            if (find_block_end(probe) == inner_end) {
                inner_if = probe;
                break;
            }
            if (probe == outer_if + 1) {
                break;
            }
        }
        if (inner_if >= lines.size()) {
            continue;
        }

        const std::string inner_indent = line_indent(lines[inner_if]);
        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(else_line),
                              candidate_lines.begin() + static_cast<std::ptrdiff_t>(outer_end));
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(inner_end),
                               inner_indent + "else");
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(inner_end + 1),
                               inner_indent + "  break");

        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_reowned_else_break_guard");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
            continue;
        }
        if (!best_candidate.empty() && !IsValidationOutcomeBetter(best_error, candidate_error)) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_reowned_else_break_guard_progress");
    }
    return true;
}

bool TryRemovePrematureYieldLoopEndBeforeBreakElse(std::string& decompiled_source,
                                                   std::string_view validation_error,
                                                   std::string* note) {
    if (validation_error.find("no loop to break") == std::string_view::npos ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 5) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const std::size_t error_index =
        reported_line == 0 ? 0 : std::min(lines.size(), reported_line) - 1;
    const std::size_t search_begin = error_index > 32 ? error_index - 32 : 0;
    const std::size_t search_end = std::min(lines.size(), error_index + 6);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(StripLineComment(line));
    };
    const auto looks_like_loop_yield = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        return trimmed == "coroutine.yield()" || StartsWith(trimmed, "coroutine.yield(") ||
               StartsWith(trimmed, "WaitFor(");
    };
    const auto looks_like_while_header = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        return StartsWith(trimmed, "while ") && trimmed.ends_with(" do");
    };

    std::string best_candidate;
    std::string best_error;
    for (std::size_t end_line = search_begin; end_line < search_end; ++end_line) {
        if (trim_line(lines[end_line]) != "end") {
            continue;
        }

        const std::size_t yield_line = FindPreviousNonEmptyLuaLine(lines, end_line);
        if (yield_line >= lines.size() || !looks_like_loop_yield(lines[yield_line])) {
            continue;
        }
        if (FindNextNonEmptyLuaLine(lines, yield_line + 1) != end_line) {
            continue;
        }

        const std::size_t while_line = FindPreviousNonEmptyLuaLine(lines, yield_line);
        if (while_line >= lines.size() || !looks_like_while_header(lines[while_line])) {
            continue;
        }

        const std::size_t continuation_line = FindNextNonEmptyLuaLine(lines, end_line + 1);
        if (continuation_line >= lines.size() ||
            !StartsWith(trim_line(lines[continuation_line]), "if ")) {
            continue;
        }

        bool saw_break = false;
        bool saw_else = false;
        for (std::size_t probe = continuation_line;
             probe < std::min(lines.size(), error_index + 24);
             ++probe) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            saw_break |= probe_trimmed == "break";
            saw_else |= probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif ");
        }
        if (!saw_break || !saw_else) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(end_line));
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note, "syntax_repaired_removed_premature_yield_loop_end");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
            continue;
        }
        if (!best_candidate.empty() && !IsValidationOutcomeBetter(best_error, candidate_error)) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note, "syntax_repaired_removed_premature_yield_loop_end_progress");
    }
    return true;
}

bool TryInsertEndBeforeReturn(std::string& decompiled_source,
                              std::string_view validation_error,
                              std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find("near 'return'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!(trimmed == "return" || StartsWith(trimmed, "return "))) {
            continue;
        }

        std::size_t previous = i;
        while (previous > 0) {
            --previous;
            const std::string_view previous_trimmed = TrimWhitespace(lines[previous]);
            if (previous_trimmed.empty()) {
                continue;
            }
            if (previous_trimmed == "end" || previous_trimmed == "else" ||
                StartsWith(previous_trimmed, "elseif ") ||
                StartsWith(previous_trimmed, "--")) {
                previous = lines.size();
            }
            break;
        }
        if (previous == lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                               indent + "end");
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_return");
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
        *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_return_progress");
    }
    return true;
}

bool TryInsertEndBeforeUntil(std::string& decompiled_source,
                             std::string_view validation_error,
                             std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find("near 'until'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!(trimmed == "until" || StartsWith(trimmed, "until "))) {
            continue;
        }

        std::size_t previous = i;
        while (previous > 0) {
            --previous;
            const std::string_view previous_trimmed = TrimWhitespace(lines[previous]);
            if (previous_trimmed.empty()) {
                continue;
            }
            if (previous_trimmed == "end" || previous_trimmed == "until" ||
                StartsWith(previous_trimmed, "until ") ||
                StartsWith(previous_trimmed, "--")) {
                previous = lines.size();
            }
            break;
        }
        if (previous == lines.size()) {
            continue;
        }

        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        for (int end_count = 1; end_count <= 4; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                                   static_cast<std::size_t>(end_count), indent + "end");
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_until");
                }
                return true;
            }

            if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
                (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
                best_candidate = std::move(candidate);
                best_error = candidate_error;
            }
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_until_progress");
    }
    return true;
}

bool TryInsertUntilBeforeTrailingEnd(std::string& decompiled_source,
                                     std::string_view validation_error,
                                     std::string* note) {
    if (validation_error.find("'until' expected") == std::string_view::npos ||
        validation_error.find("to close 'repeat'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = lines.size(); i-- > 0;) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed != "end" && trimmed != "else" && !StartsWith(trimmed, "elseif ")) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                               indent + "until true");
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_inserted_until_before_end");
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
        *note = JoinNotes(*note, "syntax_repaired_inserted_until_before_end_progress");
    }
    return true;
}

bool TryCloseReportedTopLevelFunctionBeforeSiblingBoundary(std::string& decompiled_source,
                                                           std::string_view validation_error,
                                                           std::string* note) {
    constexpr std::string_view kNeedle = "to close 'function' at line ";
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find(kNeedle) == std::string_view::npos) {
        return false;
    }

    const std::size_t open_function_line = ExtractLineNumberAfterNeedle(validation_error, kNeedle);
    if (open_function_line == 0) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (open_function_line > lines.size()) {
        return false;
    }

    const std::size_t open_line_index = open_function_line - 1;
    const std::string_view open_trimmed = TrimLuaCodeLine(lines[open_line_index]);
    if (open_trimmed.find("function(") == std::string_view::npos &&
        open_trimmed.find("function ") == std::string_view::npos) {
        return false;
    }

    const std::string open_indent(GetLuaLineIndentView(lines[open_line_index]));
    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    std::string best_candidate;
    std::string best_error;

    for (std::size_t boundary_line = open_line_index + 1;
         boundary_line < lines.size() && boundary_line <= open_line_index + 256;
         ++boundary_line) {
        if (GetLuaLineIndentView(lines[boundary_line]).size() > open_indent.size()) {
            continue;
        }

        const std::string_view boundary_trimmed = TrimLuaCodeLine(lines[boundary_line]);
        if (boundary_trimmed.empty() || boundary_trimmed == "end" || boundary_trimmed == "else" ||
            StartsWith(boundary_trimmed, "elseif ") ||
            StartsWith(boundary_trimmed, "until ")) {
            continue;
        }

        std::size_t suppressed_run_begin = lines.size();
        std::size_t suppressed_run_end = lines.size();
        const std::size_t suppressed_run_count = FindSuppressedEndRun(
            lines, boundary_line, &suppressed_run_begin, &suppressed_run_end);
        const int max_end_count =
            suppressed_run_count == 0
                ? 8
                : std::max<int>(8, std::min<int>(12, static_cast<int>(suppressed_run_count)));

        for (int end_count = 1; end_count <= max_end_count; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            if (suppressed_run_count != 0) {
                const std::size_t replace_count =
                    std::min<std::size_t>(static_cast<std::size_t>(end_count),
                                          suppressed_run_count);
                const std::size_t replace_begin = suppressed_run_end - replace_count + 1;
                for (std::size_t offset = 0; offset < replace_count; ++offset) {
                    candidate_lines[replace_begin + offset] = open_indent + "end";
                }
                if (static_cast<std::size_t>(end_count) > replace_count) {
                    candidate_lines.insert(candidate_lines.begin() +
                                               static_cast<std::ptrdiff_t>(boundary_line),
                                           static_cast<std::size_t>(end_count) - replace_count,
                                           open_indent + "end");
                }
            } else {
                candidate_lines.insert(candidate_lines.begin() +
                                           static_cast<std::ptrdiff_t>(boundary_line),
                                       static_cast<std::size_t>(end_count),
                                       open_indent + "end");
            }

            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(
                        *note,
                        "syntax_repaired_closed_reported_function_before_sibling_boundary");
                }
                return true;
            }

            const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
            if (candidate_descriptor.line < baseline_descriptor.line ||
                !IsValidationOutcomeBetter(validation_error, candidate_error) ||
                (!best_error.empty() &&
                 !IsValidationOutcomeBetter(best_error, candidate_error))) {
                continue;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_closed_reported_function_before_sibling_boundary_progress");
    }
    return true;
}

bool TryInsertEndBeforeMalformedTopLevelBoundary(std::string& decompiled_source,
                                                 std::string_view validation_error,
                                                 std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("unexpected symbol near") == std::string_view::npos &&
        validation_error.find("expected near") == std::string_view::npos &&
        validation_error.find("chunk has too many syntax levels") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const bool near_eof = validation_error.find("<eof>") != std::string_view::npos;
    const bool syntax_depth_overflow =
        validation_error.find("chunk has too many syntax levels") != std::string_view::npos;

    std::size_t top_level_indent_size = std::numeric_limits<std::size_t>::max();
    for (const std::string& line : lines) {
        const std::string_view trimmed = TrimLuaCodeLine(line);
        if (trimmed.empty()) {
            continue;
        }
        top_level_indent_size = std::min(top_level_indent_size, GetLuaLineIndentView(line).size());
    }
    if (top_level_indent_size == std::numeric_limits<std::size_t>::max()) {
        top_level_indent_size = 0;
    }
    const std::string top_level_indent(top_level_indent_size, ' ');

    const auto is_top_level_boundary_line = [&](std::size_t line_index) {
        return line_index < lines.size() &&
               GetLuaLineIndentView(lines[line_index]).size() == top_level_indent_size &&
               (LooksLikeTopLevelBoundary(TrimLuaCodeLine(lines[line_index])) ||
                IsTopLevelAnonymousFunctionBoundaryLine(lines[line_index]));
    };
    const auto local_top_level_scan_begin = [&](std::size_t boundary_line) {
        if (boundary_line == 0 || boundary_line > lines.size()) {
            return std::size_t{0};
        }
        for (std::size_t probe = boundary_line; probe-- > 0;) {
            if (is_top_level_boundary_line(probe)) {
                return probe;
            }
            if (probe == 0) {
                break;
            }
        }
        return std::size_t{0};
    };
    const auto overflow_progresses = [&](const SyntaxErrorDescriptor& baseline,
                                         const SyntaxErrorDescriptor& candidate) {
        return syntax_depth_overflow && baseline.summary == candidate.summary &&
               candidate.line > baseline.line;
    };

    std::string best_candidate;
    std::string best_error;
    for (std::size_t boundary_line = 1; boundary_line <= lines.size(); ++boundary_line) {
        const bool virtual_eof_boundary = near_eof && boundary_line == lines.size();
        if (!virtual_eof_boundary && !is_top_level_boundary_line(boundary_line)) {
            continue;
        }

        const std::size_t scan_begin = local_top_level_scan_begin(boundary_line);
        const std::size_t current_line = boundary_line + 1;
        const bool in_report_window =
            reported_line == 0 ||
            !(current_line + (syntax_depth_overflow ? 256 : 128) < reported_line ||
              current_line > reported_line + (syntax_depth_overflow ? 64 : 16));
        const bool in_tail_window = near_eof && current_line + 256 >= lines.size();
        if (!in_report_window && !in_tail_window) {
            continue;
        }

        std::size_t suppressed_close_begin = lines.size();
        std::size_t suppressed_close_end = lines.size();
        std::vector<std::string> suppressed_close_lines;
        {
            std::size_t current = boundary_line;
            while (current > 0) {
                const std::size_t probe = FindPreviousNonBlankLuaLine(lines, current);
                if (probe >= lines.size()) {
                    break;
                }

                std::string replacement;
                if (!ParseSuppressedCloseLine(lines[probe], &replacement)) {
                    break;
                }

                if (suppressed_close_end >= lines.size()) {
                    suppressed_close_end = probe;
                }
                suppressed_close_begin = probe;
                suppressed_close_lines.insert(suppressed_close_lines.begin(),
                                             std::move(replacement));
                current = probe;
                if (probe == 0) {
                    break;
                }
            }
        }

        if (!suppressed_close_lines.empty()) {
            const std::vector<std::string> stack_aware_close_lines =
                BuildStackAwareSuppressedCloseRun(
                    lines, suppressed_close_begin, suppressed_close_lines.size(), scan_begin);
            std::vector<std::vector<std::string>> close_run_variants;
            close_run_variants.push_back(stack_aware_close_lines);
            if (stack_aware_close_lines != suppressed_close_lines) {
                close_run_variants.push_back(suppressed_close_lines);
            }

            for (const std::vector<std::string>& close_run : close_run_variants) {
                std::vector<std::string> candidate_lines = lines;
                for (std::size_t offset = 0; offset < close_run.size(); ++offset) {
                    candidate_lines[suppressed_close_begin + offset] = close_run[offset];
                }

                std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
                const std::string candidate_error = ValidateLuaRepairSource(candidate);
                if (candidate_error.empty()) {
                    decompiled_source = std::move(candidate);
                    if (note != nullptr) {
                        *note = JoinNotes(
                            *note,
                            "syntax_repaired_restored_suppressed_close_run_before_malformed_top_level_boundary");
                    }
                    return true;
                }

                const SyntaxErrorDescriptor candidate_descriptor =
                    DescribeSyntaxError(candidate_error);
                const bool candidate_overflow_progress =
                    overflow_progresses(baseline_descriptor, candidate_descriptor);
                if ((!syntax_depth_overflow &&
                     candidate_descriptor.line < baseline_descriptor.line) ||
                    (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
                     !candidate_overflow_progress) ||
                    (!best_error.empty() &&
                     !IsValidationOutcomeBetter(best_error, candidate_error) &&
                     !overflow_progresses(DescribeSyntaxError(best_error), candidate_descriptor))) {
                    continue;
                }

                best_candidate = std::move(candidate);
                best_error = candidate_error;
            }
        }

        const std::size_t previous_line =
            FindPreviousNonEmptyLuaOrSuppressedEndLine(lines, boundary_line);
        if (previous_line >= lines.size()) {
            continue;
        }

        const std::vector<std::string> structured_close_lines =
            BuildStructuredMissingCloseRun(lines, boundary_line, 6, scan_begin);
        const bool missing_nested_close_first =
            !structured_close_lines.empty() &&
            GetLuaLineIndentView(structured_close_lines.front()).size() > top_level_indent_size;
        for (std::size_t close_count = 1; close_count <= structured_close_lines.size();
             ++close_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.insert(candidate_lines.begin() +
                                       static_cast<std::ptrdiff_t>(boundary_line),
                                   structured_close_lines.begin(),
                                   structured_close_lines.begin() +
                                       static_cast<std::ptrdiff_t>(close_count));

            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(
                        *note,
                        "syntax_repaired_inserted_structured_close_run_before_malformed_top_level_boundary");
                }
                return true;
            }

            const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
            const bool candidate_overflow_progress =
                overflow_progresses(baseline_descriptor, candidate_descriptor);
            if ((!syntax_depth_overflow &&
                 candidate_descriptor.line < baseline_descriptor.line) ||
                (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
                 !candidate_overflow_progress) ||
                (!best_error.empty() &&
                 !IsValidationOutcomeBetter(best_error, candidate_error) &&
                 !overflow_progresses(DescribeSyntaxError(best_error), candidate_descriptor))) {
                continue;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }

        const std::string_view previous_trimmed = TrimLuaCodeLine(lines[previous_line]);
        const bool previous_is_top_level_end =
            previous_trimmed == "end" &&
            GetLuaLineIndentView(lines[previous_line]).size() == top_level_indent_size;
        if (!previous_trimmed.empty() && LooksLikeTopLevelBoundary(previous_trimmed) &&
            !previous_is_top_level_end) {
            continue;
        }
        if (missing_nested_close_first) {
            continue;
        }

        std::size_t suppressed_run_begin = lines.size();
        std::size_t suppressed_run_end = lines.size();
        const std::size_t suppressed_run_count =
            FindSuppressedEndRun(lines, boundary_line, &suppressed_run_begin, &suppressed_run_end);

        for (int end_count = 1; end_count <= 8; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            if (suppressed_run_count != 0) {
                const std::size_t replace_count =
                    std::min<std::size_t>(static_cast<std::size_t>(end_count),
                                          suppressed_run_count);
                const std::size_t replace_begin = suppressed_run_end - replace_count + 1;
                for (std::size_t offset = 0; offset < replace_count; ++offset) {
                    candidate_lines[replace_begin + offset] = top_level_indent + "end";
                }
                if (static_cast<std::size_t>(end_count) > replace_count) {
                    candidate_lines.insert(candidate_lines.begin() +
                                               static_cast<std::ptrdiff_t>(boundary_line),
                                           static_cast<std::size_t>(end_count) - replace_count,
                                           top_level_indent + "end");
                }
            } else {
                candidate_lines.insert(candidate_lines.begin() +
                                           static_cast<std::ptrdiff_t>(boundary_line),
                                       static_cast<std::size_t>(end_count),
                                       top_level_indent + "end");
            }

            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(
                        *note,
                        "syntax_repaired_inserted_end_before_malformed_top_level_boundary");
                }
                return true;
            }

            const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
            const bool candidate_overflow_progress =
                overflow_progresses(baseline_descriptor, candidate_descriptor);
            if ((!syntax_depth_overflow &&
                 candidate_descriptor.line < baseline_descriptor.line) ||
                (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
                 !candidate_overflow_progress) ||
                (!best_error.empty() &&
                 !IsValidationOutcomeBetter(best_error, candidate_error) &&
                 !overflow_progresses(DescribeSyntaxError(best_error), candidate_descriptor))) {
                continue;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_inserted_end_before_malformed_top_level_boundary_progress");
    }
    return true;
}

bool TryMoveTrailingEndsBeforeReportedBoundary(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (reported_line > lines.size()) {
        return false;
    }

    const std::size_t boundary_line = reported_line - 1;
    const std::string_view boundary_trimmed = TrimLuaCodeLine(lines[boundary_line]);
    if (!LooksLikeTopLevelBoundary(boundary_trimmed) &&
        !StartsWith(boundary_trimmed, "local ") &&
        !StartsWith(boundary_trimmed, "function ")) {
        return false;
    }

    std::size_t first_end = FindNextNonEmptyLuaLine(lines, boundary_line + 1);
    if (first_end >= lines.size() || TrimLuaCodeLine(lines[first_end]) != "end") {
        return false;
    }

    std::size_t last_end = first_end;
    while (true) {
        const std::size_t next = FindNextNonEmptyLuaLine(lines, last_end + 1);
        if (next >= lines.size() || TrimLuaCodeLine(lines[next]) != "end") {
            break;
        }
        last_end = next;
    }

    std::vector<std::string> candidate_lines = lines;
    std::vector<std::string> end_lines(
        candidate_lines.begin() + static_cast<std::ptrdiff_t>(first_end),
        candidate_lines.begin() + static_cast<std::ptrdiff_t>(last_end + 1));
    candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(first_end),
                          candidate_lines.begin() + static_cast<std::ptrdiff_t>(last_end + 1));
    candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(boundary_line),
                           end_lines.begin(),
                           end_lines.end());

    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaRepairSource(candidate);
    if (candidate_error.empty()) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note, "syntax_repaired_moved_trailing_end_run_before_boundary");
        }
        return true;
    }

    if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
        return false;
    }

    decompiled_source = std::move(candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note, "syntax_repaired_moved_trailing_end_run_before_boundary_progress");
    }
    return true;
}

bool TryStripDanglingDoInsideTrueLoop(std::string& decompiled_source,
                                      std::string_view validation_error,
                                      std::string* note) {
    const bool missing_do_close =
        validation_error.find("'end' expected") != std::string_view::npos &&
        validation_error.find("to close 'do'") != std::string_view::npos;
    const bool syntax_depth_overflow =
        validation_error.find("chunk has too many syntax levels") != std::string_view::npos;
    if (!missing_do_close && !syntax_depth_overflow) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::size_t do_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        if (do_line >= lines.size() || TrimLuaCodeLine(lines[do_line]) != "do" ||
            GetLuaLineIndent(lines[do_line]).size() != GetLuaLineIndent(lines[line_index]).size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(do_line));
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_stripped_dangling_do_inside_true_loop");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
            continue;
        }
        if (!best_candidate.empty() && !IsValidationOutcomeBetter(best_error, candidate_error)) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_stripped_dangling_do_inside_true_loop_progress");
    }
    return true;
}

bool TryStripDanglingDoBeforeBoundary(std::string& decompiled_source,
                                      std::string_view validation_error,
                                      std::string* note) {
    const bool missing_end = validation_error.find("'end' expected") != std::string_view::npos;
    const bool syntax_depth_overflow =
        validation_error.find("chunk has too many syntax levels") != std::string_view::npos;
    if (!missing_end && !syntax_depth_overflow) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t do_line = 0; do_line < lines.size(); ++do_line) {
        if (TrimLuaCodeLine(lines[do_line]) != "do") {
            continue;
        }

        const std::string do_indent(GetLuaLineIndent(lines[do_line]));
        const std::size_t next_line = FindNextNonEmptyLuaLine(lines, do_line + 1);
        if (next_line >= lines.size() || GetLuaLineIndent(lines[next_line]).size() <= do_indent.size()) {
            continue;
        }

        bool saw_close_or_boundary = false;
        std::size_t boundary_line = lines.size();
        const std::size_t search_limit = std::min(lines.size(), do_line + 160);
        for (std::size_t probe = next_line; probe < search_limit; ++probe) {
            const std::string_view raw_trimmed = TrimWhitespace(lines[probe]);
            if (raw_trimmed.empty()) {
                continue;
            }
            if (IsSuppressedControlMarker(lines[probe])) {
                saw_close_or_boundary = true;
                continue;
            }
            if (raw_trimmed == "end" || raw_trimmed == "end," ||
                StartsWith(raw_trimmed, "until ")) {
                saw_close_or_boundary = true;
            }
            if (GetLuaLineIndent(lines[probe]).size() <= do_indent.size() &&
                (raw_trimmed == "end" || raw_trimmed == "end," ||
                 raw_trimmed == "else" || StartsWith(raw_trimmed, "elseif ") ||
                 StartsWith(raw_trimmed, "until ") || LooksLikeTopLevelBoundary(raw_trimmed))) {
                boundary_line = probe;
                break;
            }
        }
        if (!saw_close_or_boundary || boundary_line >= lines.size()) {
            continue;
        }
        if (!HasPlausibleDanglingDoOwner(lines, do_line, boundary_line)) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(do_line));
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
        const bool candidate_pushes_error_later =
            validation_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > validation_error_line;
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_stripped_dangling_do_before_boundary");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
            !candidate_pushes_error_later) {
            continue;
        }
        const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
        const bool candidate_beats_best_by_line =
            best_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > best_error_line;
        if (!best_candidate.empty() &&
            !IsValidationOutcomeBetter(best_error, candidate_error) &&
            !candidate_beats_best_by_line) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_dangling_do_before_boundary_progress");
    }
    return true;
}

bool TryStripStaleSuppressedCloseRunsBeforeTopLevelBoundaries(std::string& decompiled_source,
                                                              std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    const std::size_t top_level_indent_size = ComputeTopLevelIndentSize(lines);

    bool changed = false;
    for (std::size_t line_index = 1; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimLuaCodeLine(lines[line_index]);
        if (trimmed.empty() || GetLuaLineIndentView(lines[line_index]).size() != top_level_indent_size ||
            !LooksLikeTopLevelBoundary(trimmed)) {
            continue;
        }

        std::size_t run_begin = lines.size();
        std::size_t run_end = lines.size();
        std::size_t current = line_index;
        while (current > 0) {
            const std::size_t probe = FindPreviousNonBlankLuaLine(lines, current);
            if (probe >= lines.size() || !IsSuppressedControlMarker(lines[probe])) {
                break;
            }
            if (run_end >= lines.size()) {
                run_end = probe;
            }
            run_begin = probe;
            current = probe;
            if (probe == 0) {
                break;
            }
        }
        if (run_begin >= lines.size() || run_end >= lines.size()) {
            continue;
        }

        const std::size_t previous_real = FindPreviousNonBlankLuaLine(lines, run_begin);
        if (previous_real >= lines.size()) {
            continue;
        }
        const std::string_view previous_trimmed = TrimLuaCodeLine(lines[previous_real]);
        if (previous_trimmed != "end" && previous_trimmed != "end," &&
            !StartsWith(previous_trimmed, "until ")) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(run_begin),
                    lines.begin() + static_cast<std::ptrdiff_t>(run_end + 1));
        changed = true;
        line_index = run_begin == 0 ? 0 : run_begin - 1;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_stale_suppressed_close_runs");
    }
    return true;
}

bool TryRestoreStructuredCloseRunsBeforeTopLevelBoundaries(std::string& decompiled_source,
                                                           std::string_view validation_error,
                                                           std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("unexpected symbol near") == std::string_view::npos &&
        validation_error.find("expected near") == std::string_view::npos &&
        validation_error.find("chunk has too many syntax levels") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> baseline_lines =
        SplitLines(decompiled_source, &had_trailing_newline);
    if (baseline_lines.size() < 4) {
        return false;
    }

    const std::size_t top_level_indent_size = ComputeTopLevelIndentSize(baseline_lines);
    const auto is_top_level_boundary_line = [&](const std::vector<std::string>& lines,
                                                std::size_t line_index) {
        return line_index < lines.size() &&
               GetLuaLineIndentView(lines[line_index]).size() == top_level_indent_size &&
               LooksLikeTopLevelBoundary(TrimLuaCodeLine(lines[line_index]));
    };
    const auto local_top_level_scan_begin = [&](const std::vector<std::string>& lines,
                                                std::size_t boundary_line) -> std::size_t {
        if (boundary_line == 0 || boundary_line > lines.size()) {
            return 0;
        }
        for (std::size_t probe = boundary_line; probe-- > 0;) {
            if (is_top_level_boundary_line(lines, probe)) {
                return probe;
            }
            if (probe == 0) {
                break;
            }
        }
        return 0;
    };
    const auto build_structured_close_run =
        [&](const std::vector<std::string>& lines,
            std::size_t boundary_line,
            std::size_t scan_begin,
            std::size_t max_close_count) -> std::vector<std::string> {
            std::vector<MissingControlCloseFrame> open_controls;
            open_controls.reserve(32);

            for (std::size_t probe = std::min(scan_begin, lines.size());
                 probe < std::min(boundary_line, lines.size());
                 ++probe) {
                UpdateStructuredMissingCloseControlStack(&open_controls, lines[probe]);
            }

            std::vector<std::string> replacements;
            replacements.reserve(std::min(max_close_count, open_controls.size()));
            for (std::size_t offset = 0;
                 offset < max_close_count && offset < open_controls.size();
                 ++offset) {
                const MissingControlCloseFrame& frame =
                    open_controls[open_controls.size() - 1 - offset];
                replacements.push_back(frame.indent +
                                       (frame.kind == MissingControlCloseKind::kUntil
                                            ? "until true"
                                            : "end"));
            }
            return replacements;
        };

    std::vector<std::string> candidate_lines = baseline_lines;
    bool changed = false;
    for (std::size_t boundary_line = 1; boundary_line <= candidate_lines.size(); ++boundary_line) {
        const bool virtual_eof_boundary = boundary_line == candidate_lines.size();
        if (!virtual_eof_boundary &&
            !is_top_level_boundary_line(candidate_lines, boundary_line)) {
            continue;
        }

        const std::size_t previous_line = FindPreviousNonBlankLuaLine(candidate_lines, boundary_line);
        if (previous_line >= candidate_lines.size()) {
            continue;
        }
        const std::string_view previous_trimmed = TrimLuaCodeLine(candidate_lines[previous_line]);

        const std::size_t scan_begin = local_top_level_scan_begin(candidate_lines, boundary_line);
        const std::size_t max_structured_close_count = virtual_eof_boundary ? 8 : 4;
        std::vector<std::string> structured_close_lines =
            build_structured_close_run(candidate_lines,
                                       boundary_line,
                                       scan_begin,
                                       max_structured_close_count);
        if (structured_close_lines.empty() ||
            structured_close_lines.size() > max_structured_close_count) {
            continue;
        }
        if ((previous_trimmed == "end" || previous_trimmed == "until" ||
             StartsWith(previous_trimmed, "until ")) &&
            structured_close_lines.size() == 1 &&
            TrimLuaCodeLine(structured_close_lines.front()) == "end") {
            continue;
        }
        if (GetLuaLineIndentView(structured_close_lines.front()).size() > top_level_indent_size + 6) {
            continue;
        }

        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(boundary_line),
                               structured_close_lines.begin(),
                               structured_close_lines.end());
        changed = true;
        if (!virtual_eof_boundary) {
            boundary_line += structured_close_lines.size();
        } else {
            break;
        }
    }

    if (!changed) {
        return false;
    }

    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaRepairSource(candidate);
    if (candidate_error.empty()) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                "syntax_repaired_restored_structured_close_runs_before_top_level_boundaries");
        }
        return true;
    }

    const DecompiledMarkerSummary baseline_marker_summary =
        SummarizeDecompilerMarkers(decompiled_source);
    const DecompiledMarkerSummary candidate_marker_summary =
        SummarizeDecompilerMarkers(candidate);
    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
    const bool syntax_depth_overflow =
        validation_error.find("chunk has too many syntax levels") != std::string_view::npos;
    const bool overflow_progress =
        syntax_depth_overflow &&
        baseline_descriptor.summary == candidate_descriptor.summary &&
        candidate_descriptor.line > baseline_descriptor.line;
    if (!IsValidationOutcomeBetter(validation_error, candidate_error) && !overflow_progress &&
        !IsDecompilerMarkerSummaryBetter(baseline_marker_summary, candidate_marker_summary)) {
        return false;
    }

    decompiled_source = std::move(candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_restored_structured_close_runs_before_top_level_boundaries_progress");
    }
    return true;
}

}  // namespace refii::lua::internal
