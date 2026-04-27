#include "tools/lua_decompiler_internal.h"
#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {
namespace {

struct WaitContinuationRun {
    std::size_t line = 0;
    std::size_t start = 0;
    std::size_t end = 0;
    bool is_else = false;
    bool is_elseif = false;
    bool preceded_by_close_run = false;
};

std::string_view TrimLuaCodeLine(std::string_view line) {
    return TrimWhitespace(StripLineComment(line));
}

bool IsRepeatOrTrueWaitOwner(std::string_view line) {
    const std::string_view trimmed = TrimLuaCodeLine(line);
    return trimmed == "repeat" || trimmed == "while true do";
}

std::string_view TrimRawLuaLine(std::string_view line) {
    return TrimWhitespace(line);
}

std::size_t FindTopLevelElseInBlock(const std::vector<std::string>& lines,
                                    std::size_t start,
                                    std::size_t end,
                                    std::string_view owner_indent) {
    if (start >= end || end > lines.size()) {
        return lines.size();
    }

    int depth = 1;
    for (std::size_t probe = start + 1; probe < end; ++probe) {
        const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
        if (trimmed.empty()) {
            continue;
        }
        if (depth == 1 && GetLuaLineIndent(lines[probe]) == owner_indent &&
            trimmed == "else") {
            return probe;
        }
        depth += ComputeLuaBlockDelta(lines[probe]);
    }
    return lines.size();
}

bool FindOwnerContinuationRun(const std::vector<std::string>& lines,
                              std::size_t search_start,
                              std::string_view outer_indent,
                              std::string_view body_indent,
                              WaitContinuationRun* run) {
    if (run == nullptr) {
        return false;
    }

    std::size_t continuation_line = search_start;
    bool preceded_by_close_run = false;
    while (continuation_line < lines.size()) {
        const std::string_view trimmed = TrimLuaCodeLine(lines[continuation_line]);
        if (trimmed.empty()) {
            ++continuation_line;
            continue;
        }
        if (trimmed == "end" || trimmed == "end," || StartsWith(trimmed, "until ")) {
            preceded_by_close_run = true;
            ++continuation_line;
            continue;
        }
        break;
    }
    const std::string_view continuation_trimmed =
        continuation_line < lines.size() ? TrimLuaCodeLine(lines[continuation_line])
                                         : std::string_view();
    const bool continuation_is_else = continuation_trimmed == "else";
    const bool continuation_is_elseif = StartsWith(continuation_trimmed, "elseif ");
    const std::string continuation_indent =
        continuation_line < lines.size() ? GetLuaLineIndent(lines[continuation_line])
                                         : std::string();
    const bool continuation_indent_matches_owner = continuation_indent == outer_indent;
    const bool continuation_indent_is_dedented_owner =
        continuation_indent.size() + 2 == outer_indent.size();
    if (continuation_line >= lines.size() ||
        (!continuation_is_else && !continuation_is_elseif) ||
        (!continuation_indent_matches_owner && !continuation_indent_is_dedented_owner)) {
        return false;
    }

    const std::size_t continuation_start = FindNextNonEmptyLuaLine(lines, continuation_line + 1);
    if (continuation_start >= lines.size() ||
        !GetLuaLineIndent(lines[continuation_start]).starts_with(body_indent)) {
        return false;
    }

    std::size_t continuation_end = lines.size();
    for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
        const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
        if (trimmed.empty()) {
            continue;
        }
        if (GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
            continuation_end = probe;
            break;
        }
    }

    run->line = continuation_line;
    run->start = continuation_start;
    run->end = continuation_end;
    run->is_else = continuation_is_else;
    run->is_elseif = continuation_is_elseif;
    run->preceded_by_close_run = preceded_by_close_run;
    return true;
}

std::size_t FindPreviousNonCommentLuaLine(const std::vector<std::string>& lines,
                                          std::size_t start) {
    if (start == 0 || start > lines.size()) {
        return lines.size();
    }
    for (std::size_t probe = start; probe-- > 0;) {
        if (!TrimLuaCodeLine(lines[probe]).empty()) {
            return probe;
        }
        if (probe == 0) {
            break;
        }
    }
    return lines.size();
}

std::size_t FindNextNonCommentLuaLine(const std::vector<std::string>& lines, std::size_t start) {
    for (std::size_t probe = start; probe < lines.size(); ++probe) {
        if (!TrimLuaCodeLine(lines[probe]).empty()) {
            return probe;
        }
    }
    return lines.size();
}

std::size_t FindFirstNonCommentLuaLineInRange(const std::vector<std::string>& lines,
                                              std::size_t begin,
                                              std::size_t end) {
    for (std::size_t probe = begin; probe < end && probe < lines.size(); ++probe) {
        if (!TrimLuaCodeLine(lines[probe]).empty()) {
            return probe;
        }
    }
    return lines.size();
}

std::size_t FindLastNonCommentLuaLineInRange(const std::vector<std::string>& lines,
                                             std::size_t begin,
                                             std::size_t end) {
    if (begin >= end || end > lines.size()) {
        return lines.size();
    }
    for (std::size_t probe = end; probe-- > begin;) {
        if (!TrimLuaCodeLine(lines[probe]).empty()) {
            return probe;
        }
    }
    return lines.size();
}

std::string DedentLuaIndent(std::string indent) {
    if (indent.empty()) {
        return indent;
    }
    if (indent.back() == '\t') {
        indent.pop_back();
        return indent;
    }
    if (indent.size() >= 2 && indent.substr(indent.size() - 2) == "  ") {
        indent.resize(indent.size() - 2);
        return indent;
    }
    indent.pop_back();
    return indent;
}

bool IsTerminalBranchStatement(std::string_view trimmed) {
    return trimmed == "return" || StartsWith(trimmed, "return ") || trimmed == "break";
}

bool ParseLuaIpairsLoop(std::string_view line,
                        std::string* collection,
                        std::string* loop_variable) {
    if (collection == nullptr || loop_variable == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimLuaCodeLine(line);
    if (!StartsWith(trimmed, "for ") || !EndsWith(trimmed, " do")) {
        return false;
    }

    const std::size_t in_ipairs = trimmed.find(" in ipairs(");
    if (in_ipairs == std::string_view::npos || in_ipairs <= 4) {
        return false;
    }

    const std::size_t close_paren = trimmed.rfind(')');
    if (close_paren == std::string_view::npos || close_paren <= in_ipairs + 11 ||
        close_paren + 4 != trimmed.size()) {
        return false;
    }

    std::string_view header = TrimWhitespace(trimmed.substr(4, in_ipairs - 4));
    if (header.empty()) {
        return false;
    }

    std::string_view variable = header;
    const std::size_t last_comma = header.rfind(',');
    if (last_comma != std::string_view::npos) {
        variable = TrimWhitespace(header.substr(last_comma + 1));
    }
    if (variable.empty()) {
        return false;
    }

    const std::string_view extracted_collection =
        TrimWhitespace(trimmed.substr(in_ipairs + 11, close_paren - (in_ipairs + 11)));
    if (extracted_collection.empty()) {
        return false;
    }

    *collection = std::string(extracted_collection);
    *loop_variable = std::string(variable);
    return true;
}

}  // namespace

bool TryRewriteDanglingYieldElseWaitLoops(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::size_t if_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string if_condition;
        if (if_line >= lines.size() || !ParseLuaIfCondition(lines[if_line], &if_condition)) {
            continue;
        }

        std::size_t else_line = lines.size();
        for (std::size_t probe = if_line + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed == "else" || StartsWith(trimmed, "elseif ")) {
                else_line = probe;
                break;
            }
        }
        if (else_line >= lines.size()) {
            continue;
        }

        std::size_t first_trailing_end = lines.size();
        std::size_t trailing_end_count = 0;
        for (std::size_t probe = else_line; probe-- > if_line + 1;) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (trimmed != "end") {
                break;
            }
            first_trailing_end = probe;
            ++trailing_end_count;
        }
        if (first_trailing_end >= else_line || trailing_end_count < 2) {
            continue;
        }

        const std::size_t body_last =
            FindLastNonEmptyLuaLineInRange(lines, if_line + 1, first_trailing_end);
        if (body_last >= first_trailing_end || TrimLuaCodeLine(lines[body_last]) !=
                                                   "coroutine.yield()") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string while_body_indent = outer_indent + "  ";
        const std::string if_body_prefix = GetLuaLineIndent(lines[if_line]) + "  ";
        lines[line_index] = outer_indent + "while " + if_condition + " do";
        for (std::size_t probe = if_line + 1; probe < first_trailing_end; ++probe) {
            const std::string current_indent = GetLuaLineIndent(lines[probe]);
            if (current_indent.starts_with(if_body_prefix)) {
                lines[probe] = while_body_indent +
                               std::string(current_indent.substr(if_body_prefix.size())) +
                               std::string(lines[probe].substr(current_indent.size()));
            }
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(first_trailing_end));
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(if_line));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rewrote_dangling_wait_loop");
    }
    return true;
}

bool TryBreakRepeatedWaitElseContinuations(std::string& decompiled_source,
                                           std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (!IsRepeatOrTrueWaitOwner(lines[line_index])) {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";
        const std::string branch_indent = body_indent + "  ";

        std::size_t if_line = lines.size();
        std::size_t else_line = lines.size();
        std::size_t end_line = lines.size();
        WaitContinuationRun continuation;
        bool implicit_tail_wait = false;
        bool then_is_wait_only = false;
        bool else_is_wait_only = false;
        std::size_t then_last = lines.size();
        std::size_t else_last = lines.size();
        std::size_t yield_line = lines.size();
        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (!trimmed.empty() && GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]) != body_indent) {
                continue;
            }

            std::string ignored_condition;
            if (!ParseLuaIfCondition(lines[probe], &ignored_condition)) {
                continue;
            }

            int balance = 1;
            std::size_t candidate_else = lines.size();
            std::size_t candidate_end = lines.size();
            for (std::size_t inner = probe + 1; inner < lines.size(); ++inner) {
                const std::string_view inner_trimmed = TrimLuaCodeLine(lines[inner]);
                if (!inner_trimmed.empty() &&
                    GetLuaLineIndent(lines[inner]).size() < body_indent.size()) {
                    break;
                }
                if (balance == 1 && GetLuaLineIndent(lines[inner]) == body_indent &&
                    inner_trimmed == "else") {
                    candidate_else = inner;
                }
                balance += ComputeLuaBlockDelta(lines[inner]);
                if (balance == 0) {
                    candidate_end = inner;
                    break;
                }
            }

            if (candidate_else >= lines.size() || balance != 0 || candidate_end >= lines.size()) {
                continue;
            }

            const std::size_t candidate_then_body_start =
                FindNextNonEmptyLuaLine(lines, probe + 1);
            std::size_t candidate_yield_line = lines.size();
            std::size_t candidate_then_last = lines.size();
            std::size_t candidate_else_last = lines.size();
            bool candidate_implicit_tail_wait = false;
            bool candidate_then_is_wait_only = false;
            bool candidate_else_is_wait_only = false;
            if (candidate_else < lines.size()) {
                const std::size_t candidate_else_body_start =
                    FindNextNonEmptyLuaLine(lines, candidate_else + 1);
                candidate_then_last =
                    FindLastNonEmptyLuaLineInRange(lines, probe + 1, candidate_else);
                candidate_else_last =
                    FindLastNonEmptyLuaLineInRange(lines, candidate_else + 1, candidate_end);
                candidate_then_is_wait_only =
                    candidate_then_body_start < candidate_else &&
                    candidate_then_last == candidate_then_body_start &&
                    TrimLuaCodeLine(lines[candidate_then_body_start]) == "coroutine.yield()";
                candidate_else_is_wait_only =
                    candidate_else_body_start < candidate_end &&
                    candidate_else_last == candidate_else_body_start &&
                    TrimLuaCodeLine(lines[candidate_else_body_start]) == "coroutine.yield()";
                if (candidate_then_is_wait_only == candidate_else_is_wait_only) {
                    continue;
                }
            } else {
                if (candidate_then_body_start != candidate_end) {
                    continue;
                }
                candidate_yield_line = FindNextNonEmptyLuaLine(lines, candidate_end + 1);
                if (candidate_yield_line >= lines.size() ||
                    GetLuaLineIndent(lines[candidate_yield_line]) != body_indent ||
                    !IsLuaYieldWaitLine(lines[candidate_yield_line])) {
                    continue;
                }
                candidate_implicit_tail_wait = true;
            }

            WaitContinuationRun candidate_continuation;
            if (!FindOwnerContinuationRun(lines,
                                          (candidate_implicit_tail_wait ? candidate_yield_line
                                                                        : candidate_end) +
                                              1,
                                          outer_indent,
                                          body_indent,
                                          &candidate_continuation)) {
                continue;
            }

            if_line = probe;
            else_line = candidate_else;
            end_line = candidate_end;
            continuation = candidate_continuation;
            implicit_tail_wait = candidate_implicit_tail_wait;
            then_is_wait_only = candidate_then_is_wait_only;
            else_is_wait_only = candidate_else_is_wait_only;
            then_last = candidate_then_last;
            else_last = candidate_else_last;
            yield_line = candidate_yield_line;
            break;
        }

        if (if_line >= lines.size() || else_line >= lines.size() || end_line >= lines.size()) {
            continue;
        }

        if (continuation.start >= lines.size() ||
            !GetLuaLineIndent(lines[continuation.start]).starts_with(body_indent)) {
            continue;
        }

        if (continuation.end >= lines.size()) {
            continuation.end = lines.size();
            for (std::size_t probe = continuation.start; probe < lines.size(); ++probe) {
                const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                    continuation.end = probe;
                    break;
                }
            }
        }

        bool already_terminal = false;
        std::size_t break_insert_at = end_line;
        const std::string continuation_header = continuation.line < lines.size()
                                                    ? std::string(TrimLuaCodeLine(
                                                          lines[continuation.line]))
                                                    : std::string();
        if (!implicit_tail_wait) {
            const std::size_t terminal_branch_last = else_is_wait_only ? then_last : else_last;
            if (terminal_branch_last >= (else_is_wait_only ? else_line : end_line)) {
                continue;
            }
            const std::string_view terminal_branch_last_trimmed =
                TrimLuaCodeLine(lines[terminal_branch_last]);
            already_terminal = terminal_branch_last_trimmed == "break" ||
                               StartsWith(terminal_branch_last_trimmed, "return ");
            break_insert_at = else_is_wait_only ? else_line : end_line;
        }
        if (!already_terminal) {
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(break_insert_at),
                         branch_indent + "break");
            if (!implicit_tail_wait && break_insert_at <= else_line) {
                ++else_line;
            }
            if (break_insert_at <= end_line) {
                ++end_line;
            }
            if (implicit_tail_wait && break_insert_at <= yield_line) {
                ++yield_line;
            }
        }

        lines[line_index] = outer_indent + "while true do";
        const std::size_t continuation_shift =
            (!already_terminal && break_insert_at <= continuation.line) ? 1 : 0;
        std::size_t continuation_line_index = continuation.line + continuation_shift;
        int continuation_body_shift = static_cast<int>(continuation_shift);
        if (continuation.preceded_by_close_run && continuation.is_else) {
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(continuation_line_index));
            --continuation_body_shift;
        } else if (continuation.is_else) {
            lines[continuation_line_index] = outer_indent + "end";
        } else {
            if (continuation.preceded_by_close_run) {
                continue;
            }
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(continuation_line_index),
                         outer_indent + "end");
            ++continuation_line_index;
            lines[continuation_line_index] =
                outer_indent + "if" + continuation_header.substr(6);
            ++continuation_body_shift;
        }
        const std::size_t adjusted_continuation_start =
            static_cast<std::size_t>(static_cast<int>(continuation.start) +
                                     continuation_body_shift);
        const std::size_t adjusted_continuation_end =
            static_cast<std::size_t>(static_cast<int>(continuation.end) +
                                     continuation_body_shift);
        for (std::size_t probe = adjusted_continuation_start;
             probe < adjusted_continuation_end;
             ++probe) {
            const std::string current_indent = GetLuaLineIndent(lines[probe]);
            if (current_indent.starts_with(body_indent)) {
                lines[probe] = outer_indent +
                               std::string(current_indent.substr(body_indent.size())) +
                               std::string(lines[probe].substr(current_indent.size()));
            }
        }

        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_broke_repeated_wait_before_continuation");
    }
    return true;
}

bool TryBreakEmptyThenYieldElseContinuations(std::string& decompiled_source,
                                             std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (!IsRepeatOrTrueWaitOwner(lines[line_index])) {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string continuation_body_indent = outer_indent + "  ";

        bool owner_changed = false;
        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (!trimmed.empty() && GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                break;
            }

            const std::string owner_body_indent = GetLuaLineIndent(lines[probe]);
            if (owner_body_indent.size() <= outer_indent.size() ||
                !owner_body_indent.starts_with(continuation_body_indent)) {
                continue;
            }

            if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
                continue;
            }

            const std::string branch_indent = owner_body_indent + "  ";

            const std::size_t end_line = FindBalancedLuaBlockEnd(lines, probe);
            if (end_line >= lines.size() || GetLuaLineIndent(lines[end_line]) != owner_body_indent ||
                TrimLuaCodeLine(lines[end_line]) != "end") {
                continue;
            }

            const std::size_t then_body_start = FindNextNonEmptyLuaLine(lines, probe + 1);
            if (then_body_start != end_line) {
                continue;
            }

            const std::size_t yield_line = FindNextNonEmptyLuaLine(lines, end_line + 1);
            if (yield_line >= lines.size() ||
                GetLuaLineIndent(lines[yield_line]) != owner_body_indent ||
                !IsLuaYieldWaitLine(lines[yield_line])) {
                continue;
            }

            WaitContinuationRun continuation;
            if (!FindOwnerContinuationRun(
                    lines,
                    yield_line + 1,
                    outer_indent,
                    continuation_body_indent,
                    &continuation)) {
                continue;
            }
            const std::string continuation_header = continuation.line < lines.size()
                                                        ? std::string(TrimLuaCodeLine(
                                                              lines[continuation.line]))
                                                        : std::string();

            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(end_line),
                         branch_indent + "break");
            std::size_t shifted_continuation_line = continuation.line + 1;
            std::size_t shifted_continuation_end = continuation.end + 1;

            if (TrimLuaCodeLine(lines[line_index]) == "repeat") {
                lines[line_index] = outer_indent + "while true do";
            }
            int continuation_body_shift = 1;
            if (continuation.preceded_by_close_run && continuation.is_else) {
                lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(shifted_continuation_line));
                --shifted_continuation_end;
                --continuation_body_shift;
            } else if (continuation.is_else) {
                lines[shifted_continuation_line] = outer_indent + "end";
            } else {
                if (continuation.preceded_by_close_run) {
                    continue;
                }
                lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(shifted_continuation_line),
                             outer_indent + "end");
                ++shifted_continuation_line;
                ++shifted_continuation_end;
                lines[shifted_continuation_line] =
                    outer_indent + "if" + continuation_header.substr(6);
                ++continuation_body_shift;
            }

            const std::size_t adjusted_continuation_start =
                static_cast<std::size_t>(static_cast<int>(continuation.start) +
                                         continuation_body_shift);
            const std::size_t adjusted_continuation_end =
                static_cast<std::size_t>(static_cast<int>(shifted_continuation_end) +
                                         continuation_body_shift);
            for (std::size_t reindent_probe = adjusted_continuation_start;
                 reindent_probe < adjusted_continuation_end;
                 ++reindent_probe) {
                const std::string current_indent = GetLuaLineIndent(lines[reindent_probe]);
                if (current_indent.starts_with(continuation_body_indent)) {
                    lines[reindent_probe] =
                        outer_indent +
                        std::string(current_indent.substr(continuation_body_indent.size())) +
                        std::string(lines[reindent_probe].substr(current_indent.size()));
                }
            }

            owner_changed = true;
            changed = true;
            if (line_index != 0) {
                --line_index;
            }
            break;
        }

        if (owner_changed) {
            continue;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_broke_empty_then_yield_before_continuation");
    }
    return true;
}

bool TrySplitDuplicatedElseWaitLadders(std::string& decompiled_source,
                                       std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (!IsRepeatOrTrueWaitOwner(lines[line_index])) {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        std::size_t if_line = lines.size();
        std::size_t first_else = lines.size();
        std::size_t second_else = lines.size();
        std::size_t end_line = lines.size();
        std::string condition;
        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (!trimmed.empty() && GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]) != body_indent ||
                !ParseLuaIfCondition(lines[probe], &condition)) {
                continue;
            }

            int balance = 1;
            std::vector<std::size_t> else_lines;
            for (std::size_t inner = probe + 1; inner < lines.size(); ++inner) {
                const std::string_view inner_trimmed = TrimLuaCodeLine(lines[inner]);
                if (!inner_trimmed.empty() &&
                    GetLuaLineIndent(lines[inner]).size() < body_indent.size()) {
                    break;
                }
                if (balance == 1 && GetLuaLineIndent(lines[inner]) == body_indent &&
                    inner_trimmed == "else") {
                    else_lines.push_back(inner);
                }
                balance += ComputeLuaBlockDelta(lines[inner]);
                if (balance == 0) {
                    if (else_lines.size() >= 2) {
                        if_line = probe;
                        first_else = else_lines[0];
                        second_else = else_lines[1];
                        end_line = inner;
                    }
                    break;
                }
            }
            if (end_line < lines.size()) {
                break;
            }
        }

        if (if_line >= lines.size() || first_else >= lines.size() ||
            second_else >= lines.size() || end_line >= lines.size()) {
            continue;
        }

        const std::size_t then_body_start = FindNextNonEmptyLuaLine(lines, if_line + 1);
        const std::size_t then_last =
            FindLastNonEmptyLuaLineInRange(lines, if_line + 1, first_else);
        if (then_body_start >= first_else || then_last != then_body_start ||
            TrimLuaCodeLine(lines[then_body_start]) != "coroutine.yield()") {
            continue;
        }

        const std::size_t first_else_body_start =
            FindNextNonEmptyLuaLine(lines, first_else + 1);
        const std::size_t second_else_body_start =
            FindNextNonEmptyLuaLine(lines, second_else + 1);
        if (first_else_body_start >= second_else || second_else_body_start >= end_line ||
            !GetLuaLineIndent(lines[first_else_body_start]).starts_with(body_indent) ||
            !GetLuaLineIndent(lines[second_else_body_start]).starts_with(body_indent)) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.reserve(end_line - if_line + 4);
        replacement.push_back(body_indent + "while " + condition + " do");
        replacement.push_back(body_indent + "  coroutine.yield()");
        replacement.push_back(body_indent + "end");
        AppendReindentedLuaBlock(lines,
                                 first_else_body_start,
                                 second_else,
                                 body_indent,
                                 body_indent,
                                 &replacement,
                                 true);
        AppendReindentedLuaBlock(lines,
                                 second_else_body_start,
                                 end_line,
                                 body_indent,
                                 body_indent,
                                 &replacement,
                                 true);
        if (replacement.size() <= 3) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(if_line),
                    lines.begin() + static_cast<std::ptrdiff_t>(end_line + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(if_line),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_split_duplicated_else_wait_ladder");
    }
    return true;
}

bool TryRewriteRepeatIfYieldElseContinuations(std::string& decompiled_source,
                                              std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "repeat") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string if_indent = outer_indent + "  ";
        const std::string body_indent = if_indent + "  ";

        std::size_t if_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string condition;
        if (if_line >= lines.size() || GetLuaLineIndent(lines[if_line]) != if_indent ||
            !ParseLuaIfCondition(lines[if_line], &condition)) {
            continue;
        }

        const std::size_t else_line = FindNextNonEmptyLuaLine(lines, if_line + 1);
        const std::size_t if_end = FindBalancedLuaBlockEnd(lines, if_line);
        if (else_line >= lines.size() || if_end >= lines.size()) {
            continue;
        }

        std::size_t first_continuation_line = lines.size();
        int balance = 1;
        for (std::size_t probe = if_line + 1; probe < if_end; ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (balance == 1 && GetLuaLineIndent(lines[probe]) == if_indent &&
                (trimmed == "else" || StartsWith(trimmed, "elseif "))) {
                first_continuation_line = probe;
                break;
            }
            balance += ComputeLuaBlockDelta(lines[probe]);
        }
        if (first_continuation_line >= if_end) {
            continue;
        }

        const std::size_t then_last =
            FindLastNonEmptyLuaLineInRange(lines, if_line + 1, first_continuation_line);
        if (then_last >= first_continuation_line ||
            !IsLuaYieldWaitLine(lines[then_last])) {
            continue;
        }

        std::size_t close_line = lines.size();
        bool saw_until = false;
        for (std::size_t probe = if_end + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < outer_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]) != outer_indent) {
                continue;
            }
            if (StartsWith(trimmed, "until ")) {
                saw_until = true;
                break;
            }
            if (trimmed == "end") {
                close_line = probe;
                break;
            }
        }
        if (saw_until || close_line >= lines.size()) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.push_back(outer_indent + "while " + condition + " do");
        AppendReindentedLuaBlock(lines,
                                 if_line + 1,
                                 first_continuation_line,
                                 body_indent,
                                 outer_indent + "  ",
                                 &replacement,
                                 true);
        replacement.push_back(outer_indent + "end");
        const std::string_view continuation_trimmed =
            TrimLuaCodeLine(lines[first_continuation_line]);
        if (StartsWith(continuation_trimmed, "elseif ")) {
            if (!EndsWith(continuation_trimmed, " then")) {
                continue;
            }
            replacement.push_back(outer_indent + "if " +
                                  std::string(continuation_trimmed.substr(7)));
            AppendReindentedLuaBlock(lines,
                                     first_continuation_line + 1,
                                     if_end + 1,
                                     if_indent,
                                     outer_indent,
                                     &replacement,
                                     true);
        } else {
            const std::size_t else_body_start =
                FindNextNonEmptyLuaLine(lines, first_continuation_line + 1);
            if (else_body_start >= if_end) {
                continue;
            }
            AppendReindentedLuaBlock(lines,
                                     else_body_start,
                                     if_end,
                                     body_indent,
                                     outer_indent,
                                     &replacement,
                                     true);
        }
        if (replacement.size() <= 2) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(close_line + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rewrote_repeat_if_yield_else");
    }
    return true;
}

bool TryRewriteImmediateYieldElseWaitLoops(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string condition;
        if (!ParseLuaIfCondition(lines[line_index], &condition)) {
            continue;
        }

        const std::size_t yield_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        if (yield_line >= lines.size() || TrimLuaCodeLine(lines[yield_line]) !=
                                               "coroutine.yield()") {
            continue;
        }

        const std::size_t else_line = FindNextNonEmptyLuaLine(lines, yield_line + 1);
        if (else_line >= lines.size() || TrimLuaCodeLine(lines[else_line]) != "else" ||
            GetLuaLineIndent(lines[else_line]) != GetLuaLineIndent(lines[line_index])) {
            continue;
        }

        const std::size_t end_line = FindBalancedLuaBlockEnd(lines, line_index);
        if (end_line >= lines.size()) {
            continue;
        }

        const std::size_t then_last =
            FindLastNonEmptyLuaLineInRange(lines, line_index + 1, else_line);
        if (then_last != yield_line) {
            continue;
        }

        const std::size_t else_body_start = FindNextNonEmptyLuaLine(lines, else_line + 1);
        if (else_body_start >= end_line) {
            continue;
        }

        const std::string indent = GetLuaLineIndent(lines[line_index]);
        const std::string body_indent = indent + "  ";
        if (!GetLuaLineIndent(lines[else_body_start]).starts_with(body_indent)) {
            continue;
        }

        std::vector<std::string> replacement = {
            indent + "while " + condition + " do",
            body_indent + "coroutine.yield()",
            indent + "end",
        };
        AppendReindentedLuaBlock(lines,
                                 else_body_start,
                                 end_line,
                                 body_indent,
                                 indent,
                                 &replacement,
                                 true);
        if (replacement.size() <= 3) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(end_line + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rewrote_immediate_wait_loop");
    }
    return true;
}

bool TryStripMalformedRepeatWaitWrappers(std::string& decompiled_source,
                                         std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "repeat") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        const std::size_t wait_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string wait_condition;
        if (wait_line >= lines.size() || GetLuaLineIndent(lines[wait_line]) != body_indent ||
            !ParseLuaWhileCondition(lines[wait_line], &wait_condition) ||
            wait_condition == "true") {
            continue;
        }

        const std::size_t yield_line = FindNextNonEmptyLuaLine(lines, wait_line + 1);
        if (yield_line >= lines.size() || !IsLuaYieldWaitLine(lines[yield_line])) {
            continue;
        }

        const std::size_t wait_end = FindBalancedLuaBlockEnd(lines, wait_line);
        if (wait_end >= lines.size()) {
            continue;
        }

        const std::size_t continuation_start = FindNextNonEmptyLuaLine(lines, wait_end + 1);
        if (continuation_start >= lines.size() ||
            (continuation_start == wait_end + 1 && TrimLuaCodeLine(lines[continuation_start]).empty()) ||
            !GetLuaLineIndent(lines[continuation_start]).starts_with(body_indent)) {
            continue;
        }

        std::size_t close_line = lines.size();
        bool saw_until = false;
        for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < outer_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]) != outer_indent) {
                continue;
            }
            if (StartsWith(trimmed, "until ")) {
                saw_until = true;
                break;
            }
            if (trimmed == "end") {
                close_line = probe;
                break;
            }
        }
        if (saw_until || close_line >= lines.size()) {
            continue;
        }

        bool saw_real_continuation = false;
        for (std::size_t probe = continuation_start; probe < close_line; ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).starts_with(body_indent)) {
                saw_real_continuation = true;
                break;
            }
        }
        if (!saw_real_continuation) {
            continue;
        }

        std::size_t erase_close_start = close_line;
        while (erase_close_start > continuation_start) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[erase_close_start - 1]);
            if (!trimmed.empty() && !IsSuppressedControlMarker(lines[erase_close_start - 1])) {
                break;
            }
            --erase_close_start;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(erase_close_start),
                    lines.begin() + static_cast<std::ptrdiff_t>(close_line + 1));
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_malformed_repeat_wait_wrapper");
    }
    return true;
}

bool TryRewriteBalancedOuterTrueGuardedWaitBlocks(std::string& decompiled_source,
                                                  std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::size_t outer_end = FindBalancedLuaBlockEnd(lines, line_index);
        if (outer_end >= lines.size() || outer_end <= line_index + 4 ||
            outer_end - line_index > 160) {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";
        const std::size_t if_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string if_condition;
        if (if_line >= lines.size() || GetLuaLineIndent(lines[if_line]) != body_indent) {
            continue;
        }

        if (!ParseLuaIfCondition(lines[if_line], &if_condition)) {
            std::string wait_condition;
            if (!ParseLuaWhileCondition(lines[if_line], &wait_condition)) {
                continue;
            }

            const std::size_t wait_end = FindBalancedLuaBlockEnd(lines, if_line);
            if (wait_end >= outer_end) {
                continue;
            }

            bool found_yield = false;
            for (std::size_t probe = if_line + 1; probe < wait_end; ++probe) {
                if (TrimLuaCodeLine(lines[probe]) == "coroutine.yield()") {
                    found_yield = true;
                    break;
                }
            }
            if (!found_yield) {
                continue;
            }

            const std::size_t continuation_start = FindNextNonEmptyLuaLine(lines, wait_end + 1);
            if (continuation_start >= outer_end ||
                GetLuaLineIndent(lines[continuation_start]) != body_indent) {
                continue;
            }

            std::vector<std::string> replacement;
            AppendReindentedLuaBlock(
                lines, line_index + 1, outer_end, body_indent, outer_indent, &replacement, true);
            if (replacement.empty()) {
                continue;
            }

            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                        lines.begin() + static_cast<std::ptrdiff_t>(outer_end + 1));
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                         replacement.begin(),
                         replacement.end());
            changed = true;
            if (line_index != 0) {
                --line_index;
            }
            continue;
        }

        const std::size_t if_end = FindBalancedLuaBlockEnd(lines, if_line);
        if (if_end >= outer_end) {
            continue;
        }

        const std::size_t else_line = FindTopLevelElseInBlock(lines, if_line, if_end, body_indent);
        if (else_line >= if_end) {
            continue;
        }

        const std::size_t then_start = FindFirstNonEmptyLuaLineInRange(lines, if_line + 1, else_line);
        const std::size_t else_start = FindFirstNonEmptyLuaLineInRange(lines, else_line + 1, if_end);
        std::string then_wait_condition;
        std::string else_wait_condition;
        if (then_start >= else_line || else_start >= if_end ||
            !ParseLuaWhileCondition(lines[then_start], &then_wait_condition) ||
            !ParseLuaWhileCondition(lines[else_start], &else_wait_condition)) {
            continue;
        }

        const std::size_t then_wait_end = FindBalancedLuaBlockEnd(lines, then_start);
        const std::size_t else_wait_end = FindBalancedLuaBlockEnd(lines, else_start);
        if (then_wait_end >= else_line || else_wait_end >= if_end) {
            continue;
        }

        const std::size_t then_wait_body = FindNextNonEmptyLuaLine(lines, then_start + 1);
        const std::size_t else_wait_body = FindNextNonEmptyLuaLine(lines, else_start + 1);
        if (then_wait_body >= then_wait_end || TrimLuaCodeLine(lines[then_wait_body]) !=
                                                   "coroutine.yield()" ||
            else_wait_body >= else_wait_end || TrimLuaCodeLine(lines[else_wait_body]) !=
                                                   "coroutine.yield()") {
            continue;
        }

        const std::size_t continuation_start = FindNextNonEmptyLuaLine(lines, if_end + 1);
        if (continuation_start >= outer_end ||
            GetLuaLineIndent(lines[continuation_start]) != body_indent) {
            continue;
        }

        std::vector<std::string> replacement;
        AppendReindentedLuaBlock(
            lines, line_index + 1, outer_end, body_indent, outer_indent, &replacement, true);
        if (replacement.empty()) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(outer_end + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_balanced_true_wait_wrapper");
    }
    return true;
}

bool TryRewriteNestedRepeatedWaitContinuations(std::string& decompiled_source,
                                               std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::size_t outer_end = FindBalancedLuaBlockEnd(lines, line_index);
        if (outer_end >= lines.size() || outer_end <= line_index + 6 ||
            outer_end - line_index > 96) {
            continue;
        }

        const std::string wrapper_indent(GetLuaLineIndent(lines[line_index]));
        const std::size_t wait_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string wait_condition;
        if (wait_line >= lines.size() || !ParseLuaWhileCondition(lines[wait_line], &wait_condition)) {
            continue;
        }

        const std::size_t wait_end = FindBalancedLuaBlockEnd(lines, wait_line);
        if (wait_end >= outer_end) {
            continue;
        }

        const std::size_t yield_line = FindNextNonEmptyLuaLine(lines, wait_line + 1);
        if (yield_line >= wait_end || TrimLuaCodeLine(lines[yield_line]) != "coroutine.yield()") {
            continue;
        }

        std::size_t nested_wrapper = lines.size();
        for (std::size_t probe = yield_line + 1; probe < wait_end; ++probe) {
            if (TrimLuaCodeLine(lines[probe]) == "while true do") {
                nested_wrapper = probe;
                break;
            }
        }
        if (nested_wrapper >= wait_end) {
            continue;
        }

        const std::size_t nested_if = FindNextNonEmptyLuaLine(lines, nested_wrapper + 1);
        std::string nested_condition;
        if (nested_if >= lines.size() || !ParseLuaIfCondition(lines[nested_if], &nested_condition) ||
            nested_condition != wait_condition) {
            continue;
        }

        const std::size_t nested_if_end = FindBalancedLuaBlockEnd(lines, nested_if);
        const std::size_t nested_end = FindBalancedLuaBlockEnd(lines, nested_wrapper);
        if (nested_if_end >= nested_end || nested_end >= outer_end) {
            continue;
        }

        const std::size_t nested_if_body =
            FindFirstNonEmptyLuaLineInRange(lines, nested_if + 1, nested_if_end);
        if (nested_if_body >= nested_if_end || TrimLuaCodeLine(lines[nested_if_body]) !=
                                                   "coroutine.yield()" ||
            FindLastNonEmptyLuaLineInRange(lines, nested_if + 1, nested_if_end) !=
                nested_if_body) {
            continue;
        }

        const std::string wait_body_prefix = GetLuaLineIndent(lines[wait_line]) + "  ";
        const std::string nested_body_prefix = GetLuaLineIndent(lines[nested_wrapper]) + "  ";
        std::vector<std::string> replacement = {
            wrapper_indent + "while " + wait_condition + " do",
            wrapper_indent + "  coroutine.yield()",
            wrapper_indent + "end",
        };
        AppendReindentedLuaBlock(lines,
                                 yield_line + 1,
                                 nested_wrapper,
                                 wait_body_prefix,
                                 wrapper_indent,
                                 &replacement,
                                 true);
        replacement.push_back(wrapper_indent + "while " + wait_condition + " do");
        replacement.push_back(wrapper_indent + "  coroutine.yield()");
        replacement.push_back(wrapper_indent + "end");
        AppendReindentedLuaBlock(lines,
                                 nested_if_end + 1,
                                 nested_end,
                                 nested_body_prefix,
                                 wrapper_indent,
                                 &replacement,
                                 true);

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(outer_end + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rebalanced_nested_wait_continuations");
    }
    return true;
}

bool TryBreakSuppressedElseWaitContinuations(std::string& decompiled_source,
                                             std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";
        const std::string branch_indent = body_indent + "  ";

        std::size_t if_line = lines.size();
        std::size_t else_line = lines.size();
        std::size_t end_line = lines.size();
        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            const std::string_view raw_trimmed = TrimRawLuaLine(lines[probe]);
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (!trimmed.empty() && GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]) != body_indent) {
                continue;
            }

            std::string ignored_condition;
            if (!ParseLuaIfCondition(lines[probe], &ignored_condition)) {
                if (raw_trimmed == "-- refii-suppressed-control: else") {
                    break;
                }
                continue;
            }

            int balance = 1;
            std::size_t candidate_else = lines.size();
            for (std::size_t inner = probe + 1; inner < lines.size(); ++inner) {
                const std::string_view inner_trimmed = TrimLuaCodeLine(lines[inner]);
                if (!inner_trimmed.empty() &&
                    GetLuaLineIndent(lines[inner]).size() < body_indent.size()) {
                    break;
                }
                if (balance == 1 && GetLuaLineIndent(lines[inner]) == body_indent &&
                    inner_trimmed == "else") {
                    candidate_else = inner;
                }
                balance += ComputeLuaBlockDelta(lines[inner]);
                if (balance == 0) {
                    if_line = probe;
                    else_line = candidate_else;
                    end_line = inner;
                    break;
                }
            }
            if (end_line < lines.size()) {
                break;
            }
        }

        if (if_line >= lines.size() || else_line >= lines.size() || end_line >= lines.size()) {
            continue;
        }

        const std::size_t else_body_start = FindNextNonEmptyLuaLine(lines, else_line + 1);
        if (else_body_start >= end_line || TrimLuaCodeLine(lines[else_body_start]) !=
                                               "coroutine.yield()" ||
            FindLastNonEmptyLuaLineInRange(lines, else_line + 1, end_line) != else_body_start) {
            continue;
        }

        const std::size_t suppressed_else_line =
            FindNextNonEmptyLuaLine(lines, end_line + 1, true);
        if (suppressed_else_line >= lines.size() ||
            TrimRawLuaLine(lines[suppressed_else_line]) != "-- refii-suppressed-control: else" ||
            GetLuaLineIndent(lines[suppressed_else_line]) != outer_indent) {
            continue;
        }

        const std::size_t continuation_start =
            FindNextNonEmptyLuaLine(lines, suppressed_else_line + 1, true);
        if (continuation_start >= lines.size() ||
            !GetLuaLineIndent(lines[continuation_start]).starts_with(body_indent)) {
            continue;
        }

        std::size_t continuation_end = lines.size();
        for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                continuation_end = probe;
                break;
            }
        }

        const std::size_t then_last = FindLastNonEmptyLuaLineInRange(lines, if_line + 1, else_line);
        if (then_last >= else_line) {
            continue;
        }
        const std::string_view then_last_trimmed = TrimLuaCodeLine(lines[then_last]);
        const bool already_terminal =
            then_last_trimmed == "break" || StartsWith(then_last_trimmed, "return ");
        if (!already_terminal) {
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(else_line),
                         branch_indent + "break");
            ++else_line;
            ++end_line;
        }

        const std::size_t continuation_shift = already_terminal ? 0 : 1;
        lines[suppressed_else_line + continuation_shift] = outer_indent + "end";
        const std::size_t adjusted_continuation_start = continuation_start + continuation_shift;
        const std::size_t adjusted_continuation_end =
            continuation_end == lines.size() ? continuation_end : continuation_end + continuation_shift;
        for (std::size_t probe = adjusted_continuation_start;
             probe < adjusted_continuation_end;
             ++probe) {
            const std::string current_indent = GetLuaLineIndent(lines[probe]);
            if (current_indent.starts_with(body_indent)) {
                lines[probe] = outer_indent +
                               std::string(current_indent.substr(body_indent.size())) +
                               std::string(lines[probe].substr(current_indent.size()));
            }
        }

        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_broke_wait_loop_before_suppressed_else");
    }
    return true;
}

bool TryRewriteSuppressedElseSentinelWhileLoops(std::string& decompiled_source,
                                                std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";
        const std::size_t outer_end = FindBalancedLuaBlockEnd(lines, line_index);
        const std::size_t search_limit =
            outer_end < lines.size() ? outer_end : std::min(lines.size(), line_index + 96);

        std::size_t if_line = lines.size();
        std::size_t if_end = lines.size();
        std::size_t suppressed_else_line = lines.size();
        std::string sentinel_condition;
        for (std::size_t probe = line_index + 1; probe < search_limit; ++probe) {
            if (GetLuaLineIndent(lines[probe]) != body_indent) {
                continue;
            }

            if (!ParseLuaIfCondition(lines[probe], &sentinel_condition)) {
                continue;
            }

            const std::size_t candidate_if_end = FindBalancedLuaBlockEnd(lines, probe);
            if (candidate_if_end >= lines.size() || candidate_if_end >= search_limit) {
                continue;
            }

            const std::size_t if_body_first =
                FindFirstNonEmptyLuaLineInRange(lines, probe + 1, candidate_if_end + 1);
            if (if_body_first != candidate_if_end) {
                continue;
            }

            std::size_t candidate_suppressed_else = lines.size();
            for (std::size_t inner = candidate_if_end + 1; inner < search_limit; ++inner) {
                const std::string_view raw_trimmed = TrimRawLuaLine(lines[inner]);
                if (raw_trimmed.empty()) {
                    continue;
                }
                if (IsSuppressedElseControlMarker(lines[inner]) &&
                    GetLuaLineIndent(lines[inner]).size() <= outer_indent.size()) {
                    candidate_suppressed_else = inner;
                    break;
                }
                if (GetLuaLineIndent(lines[inner]).size() <= outer_indent.size() &&
                    !IsSuppressedControlMarker(lines[inner])) {
                    break;
                }
            }
            if (candidate_suppressed_else >= lines.size() ||
                candidate_suppressed_else >= search_limit) {
                continue;
            }

            bool saw_wait = false;
            for (std::size_t inner = candidate_if_end + 1; inner < candidate_suppressed_else;
                 ++inner) {
                if (IsLuaYieldWaitLine(lines[inner])) {
                    saw_wait = true;
                    break;
                }
            }
            if (!saw_wait) {
                continue;
            }

            if_line = probe;
            if_end = candidate_if_end;
            suppressed_else_line = candidate_suppressed_else;
            break;
        }

        if (if_line >= lines.size() || if_end >= lines.size() ||
            suppressed_else_line >= lines.size()) {
            continue;
        }

        std::size_t continuation_start =
            FindNextNonEmptyLuaLine(lines, suppressed_else_line + 1, true);
        std::size_t continuation_end = outer_end;
        std::size_t replace_end = outer_end;
        bool has_real_continuation = false;
        if (outer_end >= lines.size()) {
            continuation_end = lines.size();
            replace_end = suppressed_else_line;
            if (continuation_start < lines.size() &&
                GetLuaLineIndent(lines[continuation_start]).starts_with(body_indent)) {
                has_real_continuation = true;
                for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
                    const std::string_view raw_trimmed = TrimRawLuaLine(lines[probe]);
                    if (raw_trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                        continue;
                    }
                    if (GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                        continuation_end = probe;
                        break;
                    }
                }
                replace_end =
                    continuation_end == lines.size() ? lines.size() - 1 : continuation_end - 1;
            } else {
                continuation_start = lines.size();
                continuation_end = lines.size();
            }
        } else if (continuation_start < outer_end) {
            continuation_end = outer_end;
            has_real_continuation = true;
        }

        if (replace_end < line_index || replace_end >= lines.size()) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.reserve((replace_end - line_index) + 4);
        replacement.push_back(outer_indent + "while not (" + sentinel_condition + ") do");
        AppendReindentedLuaBlock(lines,
                                 line_index + 1,
                                 if_line,
                                 body_indent,
                                 body_indent,
                                 &replacement,
                                 true);
        AppendReindentedLuaBlock(lines,
                                 if_end + 1,
                                 suppressed_else_line,
                                 body_indent,
                                 body_indent,
                                 &replacement,
                                 true);
        replacement.push_back(outer_indent + "end");
        if (has_real_continuation) {
            AppendReindentedLuaBlock(lines,
                                     continuation_start,
                                     continuation_end,
                                     body_indent,
                                     outer_indent,
                                     &replacement,
                                     true);
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(replace_end + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_rewrote_empty_guarded_true_wait_loop");
    }
    return true;
}

bool TryRewriteYieldTrappedWaitContinuations(std::string& decompiled_source,
                                             std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        const std::size_t outer_end = FindBalancedLuaBlockEnd(lines, line_index);
        const std::size_t search_limit =
            outer_end < lines.size() ? outer_end : std::min(lines.size(), line_index + 192);
        if (search_limit <= line_index + 4) {
            continue;
        }

        const std::size_t wait_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string wait_condition;
        if (wait_line >= lines.size() || GetLuaLineIndent(lines[wait_line]) != body_indent ||
            !ParseLuaWhileCondition(lines[wait_line], &wait_condition)) {
            continue;
        }

        const std::size_t wait_end = FindBalancedLuaBlockEnd(lines, wait_line);
        if (wait_end >= lines.size() || wait_end >= search_limit) {
            continue;
        }

        const std::size_t yield_line = FindNextNonEmptyLuaLine(lines, wait_line + 1);
        if (yield_line >= wait_end || !IsLuaYieldWaitLine(lines[yield_line])) {
            continue;
        }

        const std::size_t continuation_start = FindNextNonEmptyLuaLine(lines, yield_line + 1);
        if (continuation_start >= wait_end) {
            continue;
        }

        const std::string wait_body_prefix = GetLuaLineIndent(lines[wait_line]) + "  ";
        if (!GetLuaLineIndent(lines[continuation_start]).starts_with(wait_body_prefix)) {
            continue;
        }

        std::size_t continuation_end = outer_end;
        if (outer_end >= lines.size()) {
            continuation_end = lines.size();
            for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
                const std::string_view trimmed = TrimWhitespace(lines[probe]);
                if (trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                    continue;
                }
                if (GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                    continuation_end = probe;
                    break;
                }
            }
        } else {
            bool outer_has_only_inner_wait = true;
            for (std::size_t probe = wait_end + 1; probe < outer_end; ++probe) {
                if (!TrimLuaCodeLine(lines[probe]).empty()) {
                    outer_has_only_inner_wait = false;
                    break;
                }
            }
            if (!outer_has_only_inner_wait) {
                continue;
            }
        }

        std::vector<std::string> replacement = {
            outer_indent + "while " + wait_condition + " do",
            outer_indent + "  " + std::string(TrimWhitespace(lines[yield_line])),
            outer_indent + "end",
        };
        AppendReindentedLuaBlock(
            lines, continuation_start, wait_end, wait_body_prefix, outer_indent, &replacement);
        if (replacement.size() <= 3) {
            continue;
        }

        const std::size_t erase_end =
            outer_end < lines.size()
                ? outer_end + 1
                : (continuation_end == lines.size() ? lines.size() : continuation_end);
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(erase_end));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_lifted_wait_loop_continuation");
    }
    return true;
}

bool TryLiftTrueWrappedWaitLoopContinuations(std::string& decompiled_source,
                                             std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::size_t wait_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        std::string wait_condition;
        if (wait_line >= lines.size() || GetLuaLineIndent(lines[wait_line]) != outer_indent ||
            !ParseLuaWhileCondition(lines[wait_line], &wait_condition) ||
            wait_condition == "true") {
            continue;
        }

        std::size_t tail_limit = lines.size();
        for (std::size_t probe = wait_line + 1; probe < lines.size(); ++probe) {
            const std::string_view raw_trimmed = TrimWhitespace(lines[probe]);
            if (raw_trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < outer_indent.size()) {
                tail_limit = probe;
                break;
            }
        }
        if (tail_limit <= wait_line + 4) {
            continue;
        }

        std::size_t first_yield = lines.size();
        for (std::size_t probe = wait_line + 1; probe < tail_limit; ++probe) {
            if (IsLuaYieldWaitLine(lines[probe]) &&
                GetLuaLineIndent(lines[probe]).starts_with(outer_indent)) {
                first_yield = probe;
                break;
            }
        }
        if (first_yield >= tail_limit) {
            continue;
        }

        std::size_t continuation_start = lines.size();
        for (std::size_t probe = first_yield + 1; probe < tail_limit; ++probe) {
            const std::string_view raw_trimmed = TrimWhitespace(lines[probe]);
            if (raw_trimmed.empty()) {
                continue;
            }
            if (IsSuppressedControlMarker(lines[probe]) || raw_trimmed == "end" ||
                raw_trimmed == "end," || StartsWith(raw_trimmed, "until ")) {
                continue;
            }
            continuation_start = probe;
            break;
        }
        if (continuation_start >= tail_limit) {
            continue;
        }

        std::size_t second_yield = lines.size();
        std::size_t tail_close_count = 0;
        for (std::size_t probe = continuation_start; probe < tail_limit; ++probe) {
            if (!IsLuaYieldWaitLine(lines[probe]) ||
                !GetLuaLineIndent(lines[probe]).starts_with(outer_indent)) {
                continue;
            }

            bool pure_close_run = true;
            std::size_t close_count = 0;
            for (std::size_t tail_probe = probe + 1; tail_probe < tail_limit; ++tail_probe) {
                const std::string_view raw_trimmed = TrimWhitespace(lines[tail_probe]);
                if (raw_trimmed.empty()) {
                    continue;
                }
                if (IsSuppressedControlMarker(lines[tail_probe]) || raw_trimmed == "end" ||
                    raw_trimmed == "end," || StartsWith(raw_trimmed, "until ")) {
                    ++close_count;
                    continue;
                }
                pure_close_run = false;
                break;
            }
            if (!pure_close_run || close_count < 2) {
                continue;
            }

            second_yield = probe;
            tail_close_count = close_count;
        }
        if (second_yield >= tail_limit || continuation_start >= second_yield ||
            tail_close_count < 2) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.reserve((tail_limit - line_index) + 4);
        replacement.push_back(outer_indent + "while " + wait_condition + " do");
        AppendReindentedLuaBlock(lines,
                                 wait_line + 1,
                                 first_yield + 1,
                                 outer_indent,
                                 outer_indent + "  ",
                                 &replacement,
                                 true);
        replacement.push_back(outer_indent + "end");
        AppendReindentedLuaBlock(lines,
                                 continuation_start,
                                 second_yield,
                                 outer_indent,
                                 outer_indent,
                                 &replacement,
                                 true);
        replacement.push_back(outer_indent + "coroutine.yield()");

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(tail_limit));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_lifted_true_wrapped_wait_continuation");
    }
    return true;
}

bool TryLiftMalformedYieldWaitContinuations(std::string& decompiled_source,
                                            std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string wait_condition;
        if (!ParseLuaWhileCondition(lines[line_index], &wait_condition) ||
            wait_condition == "true") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        const std::size_t yield_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        if (yield_line >= lines.size() || GetLuaLineIndent(lines[yield_line]) != body_indent ||
            !IsLuaYieldWaitLine(lines[yield_line])) {
            continue;
        }

        const std::size_t continuation_start = FindNextNonEmptyLuaLine(lines, yield_line + 1);
        if (continuation_start >= lines.size() ||
            !GetLuaLineIndent(lines[continuation_start]).starts_with(body_indent) ||
            IsSuppressedControlMarker(lines[continuation_start])) {
            continue;
        }

        const std::string_view continuation_trimmed = TrimLuaCodeLine(lines[continuation_start]);
        if (continuation_trimmed.empty() || continuation_trimmed == "end" ||
            continuation_trimmed == "else" || StartsWith(continuation_trimmed, "elseif ") ||
            StartsWith(continuation_trimmed, "until ")) {
            continue;
        }

        std::size_t continuation_end = lines.size();
        const std::size_t balanced_end = FindBalancedLuaBlockEnd(lines, line_index);
        bool saw_trailing_marker = false;
        for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
            const std::string_view raw_trimmed = TrimWhitespace(lines[probe]);
            if (raw_trimmed.empty()) {
                continue;
            }
            if (IsSuppressedControlMarker(lines[probe])) {
                continuation_end = probe;
                saw_trailing_marker = true;
                break;
            }
            if (probe > continuation_start &&
                GetLuaLineIndent(lines[probe]).size() < outer_indent.size() &&
                LooksLikeTopLevelBoundary(raw_trimmed)) {
                continuation_end = probe;
                break;
            }
        }
        const bool balanced_continuation =
            balanced_end < lines.size() && continuation_start < balanced_end;
        if (!saw_trailing_marker && balanced_continuation) {
            continuation_end = balanced_end;
        }
        if ((!saw_trailing_marker && !balanced_continuation) ||
            continuation_end <= continuation_start) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.reserve((continuation_end - continuation_start) + 3);
        replacement.push_back(lines[line_index]);
        replacement.push_back(body_indent + std::string(TrimWhitespace(lines[yield_line])));
        replacement.push_back(outer_indent + "end");
        AppendReindentedLuaBlock(lines,
                                 continuation_start,
                                 continuation_end,
                                 body_indent,
                                 outer_indent,
                                 &replacement,
                                 true);

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(continuation_end));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_lifted_malformed_yield_wait_continuation");
    }
    return true;
}

bool TryFlattenDegenerateTrueWaitWrappers(std::string& decompiled_source,
                                          std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";
        const std::size_t outer_end = FindBalancedLuaBlockEnd(lines, line_index);
        if (outer_end >= lines.size()) {
            continue;
        }

        const std::size_t first_body_line = FindNextNonEmptyLuaLine(lines, line_index + 1);
        if (first_body_line >= lines.size() ||
            GetLuaLineIndent(lines[first_body_line]) != body_indent) {
            continue;
        }

        std::string first_wait_condition;
        if (!ParseLuaWhileCondition(lines[first_body_line], &first_wait_condition) ||
            first_wait_condition == "true") {
            continue;
        }

        const std::size_t first_wait_end = FindBalancedLuaBlockEnd(lines, first_body_line);
        if (first_wait_end >= lines.size()) {
            continue;
        }

        std::size_t yield_line = lines.size();
        std::size_t tail_limit = lines.size();
        for (std::size_t probe = first_wait_end + 1; probe < lines.size(); ++probe) {
            if (!IsLuaYieldWaitLine(lines[probe]) || GetLuaLineIndent(lines[probe]) != body_indent) {
                continue;
            }

            bool saw_close_after_yield = false;
            bool tail_is_pure_close_run = true;
            std::size_t candidate_tail_limit = lines.size();
            for (std::size_t tail_probe = probe + 1; tail_probe < lines.size(); ++tail_probe) {
                const std::string_view raw_trimmed = TrimWhitespace(lines[tail_probe]);
                if (raw_trimmed.empty()) {
                    continue;
                }
                if (IsSuppressedControlMarker(lines[tail_probe]) ||
                    raw_trimmed == "end" || raw_trimmed == "end,") {
                    saw_close_after_yield = true;
                    continue;
                }
                if (GetLuaLineIndent(lines[tail_probe]).size() <= outer_indent.size() &&
                    LooksLikeTopLevelBoundary(raw_trimmed)) {
                    candidate_tail_limit = tail_probe;
                    break;
                }
                tail_is_pure_close_run = false;
                break;
            }

            if (!tail_is_pure_close_run || !saw_close_after_yield ||
                candidate_tail_limit <= probe + 1) {
                continue;
            }

            yield_line = probe;
            tail_limit = candidate_tail_limit;
        }

        if (yield_line < lines.size() && tail_limit > yield_line + 1) {
            std::vector<std::string> replacement;
            replacement.reserve(yield_line - line_index);
            AppendReindentedLuaBlock(lines,
                                     line_index + 1,
                                     yield_line + 1,
                                     body_indent,
                                     outer_indent,
                                     &replacement,
                                     true);
            if (replacement.empty()) {
                continue;
            }

            std::size_t consumed_close_lines = 0;
            for (std::size_t probe = yield_line + 1; probe < tail_limit; ++probe) {
                const std::string_view raw_trimmed = TrimWhitespace(lines[probe]);
                if (raw_trimmed.empty()) {
                    continue;
                }
                if (!(IsSuppressedControlMarker(lines[probe]) ||
                      raw_trimmed == "end" || raw_trimmed == "end,")) {
                    consumed_close_lines = 0;
                    break;
                }
                ++consumed_close_lines;
            }
            if (consumed_close_lines == 0) {
                continue;
            }

            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                        lines.begin() + static_cast<std::ptrdiff_t>(tail_limit));
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                         replacement.begin(),
                         replacement.end());
            changed = true;
            if (line_index != 0) {
                --line_index;
            }
            continue;
        }

        if (validation_error.empty()) {
            continue;
        }

        std::size_t top_level_wait_count = 0;
        bool saw_invalid_top_level_body = false;
        for (std::size_t probe = line_index + 1; probe < outer_end;) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                ++probe;
                continue;
            }
            if (GetLuaLineIndent(lines[probe]).size() < body_indent.size()) {
                break;
            }
            if (GetLuaLineIndent(lines[probe]) != body_indent) {
                ++probe;
                continue;
            }

            std::string wait_condition;
            if (ParseLuaWhileCondition(lines[probe], &wait_condition) &&
                wait_condition != "true") {
                const std::size_t wait_end = FindBalancedLuaBlockEnd(lines, probe);
                const std::size_t wait_body_start =
                    FindFirstNonEmptyLuaLineInRange(lines, probe + 1, wait_end);
                const std::size_t wait_body_last =
                    FindLastNonEmptyLuaLineInRange(lines, probe + 1, wait_end);
                if (wait_end >= outer_end || wait_body_start >= wait_end ||
                    wait_body_last != wait_body_start ||
                    !IsLuaYieldWaitLine(lines[wait_body_start])) {
                    saw_invalid_top_level_body = true;
                    break;
                }
                ++top_level_wait_count;
                probe = wait_end + 1;
                continue;
            }

            if (IsLuaYieldWaitLine(lines[probe]) || trimmed == "break" ||
                StartsWith(trimmed, "return ")) {
                saw_invalid_top_level_body = true;
                break;
            }

            ++probe;
        }
        if (saw_invalid_top_level_body || top_level_wait_count < 2) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        std::vector<std::string> replacement;
        AppendReindentedLuaBlock(lines,
                                 line_index + 1,
                                 outer_end,
                                 body_indent,
                                 outer_indent,
                                 &replacement,
                                 true);
        if (replacement.empty()) {
            continue;
        }

        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                              candidate_lines.begin() + static_cast<std::ptrdiff_t>(outer_end + 1));
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                               replacement.begin(),
                               replacement.end());
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (!candidate_error.empty() &&
            !IsValidationOutcomeBetter(validation_error, candidate_error)) {
            continue;
        }

        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                candidate_error.empty()
                    ? "syntax_repaired_flattened_sequential_true_wait_wrapper"
                    : "syntax_repaired_flattened_sequential_true_wait_wrapper_progress");
        }
        return true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_flattened_true_wait_wrapper");
    }
    return true;
}

bool TryLiftTrailingYieldAcrossSuppressedCloseRun(std::string& decompiled_source,
                                                  std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    enum class MissingControlCloseKind {
        kEnd,
        kUntil,
    };
    struct MissingControlCloseFrame {
        MissingControlCloseKind kind;
        std::string indent;
        std::size_t line_index = 0;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        std::size_t yield_line = lines.size();
        std::size_t tail_limit = lines.size();
        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            if (!IsLuaYieldWaitLine(lines[probe])) {
                continue;
            }

            bool saw_close_after_yield = false;
            bool tail_is_pure_close_run = true;
            std::size_t candidate_tail_limit = lines.size();
            for (std::size_t tail_probe = probe + 1; tail_probe < lines.size(); ++tail_probe) {
                const std::string_view raw_trimmed = TrimRawLuaLine(lines[tail_probe]);
                if (raw_trimmed.empty()) {
                    continue;
                }
                if (IsSuppressedControlMarker(lines[tail_probe]) ||
                    raw_trimmed == "end" || raw_trimmed == "end,") {
                    saw_close_after_yield = true;
                    continue;
                }
                if (GetLuaLineIndent(lines[tail_probe]).size() <= outer_indent.size() &&
                    LooksLikeTopLevelBoundary(raw_trimmed)) {
                    candidate_tail_limit = tail_probe;
                    break;
                }
                tail_is_pure_close_run = false;
                break;
            }

            if (!tail_is_pure_close_run || !saw_close_after_yield ||
                candidate_tail_limit <= probe + 1) {
                continue;
            }

            yield_line = probe;
            tail_limit = candidate_tail_limit;
        }

        if (yield_line >= lines.size() || tail_limit <= yield_line + 1) {
            continue;
        }

        std::vector<MissingControlCloseFrame> open_controls;
        open_controls.reserve(32);
        const auto pop_last_kind = [&](MissingControlCloseKind kind) {
            for (std::size_t i = open_controls.size(); i-- > 0;) {
                if (open_controls[i].kind == kind) {
                    open_controls.erase(open_controls.begin() +
                                        static_cast<std::ptrdiff_t>(i));
                    return;
                }
            }
        };
        for (std::size_t probe = line_index; probe < yield_line; ++probe) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty() || trimmed == "else" || StartsWith(trimmed, "elseif ")) {
                continue;
            }
            if (trimmed == "repeat") {
                open_controls.push_back({MissingControlCloseKind::kUntil,
                                         std::string(GetLuaLineIndent(lines[probe])),
                                         probe});
                continue;
            }
            if (trimmed == "until" || StartsWith(trimmed, "until ")) {
                pop_last_kind(MissingControlCloseKind::kUntil);
                continue;
            }
            if (trimmed == "end" || trimmed == "end,") {
                pop_last_kind(MissingControlCloseKind::kEnd);
                continue;
            }
            if (trimmed == "do" || StartsWith(trimmed, "if ") || StartsWith(trimmed, "for ") ||
                StartsWith(trimmed, "while ") || StartsWith(trimmed, "function ") ||
                StartsWith(trimmed, "local function ") ||
                StartsWith(trimmed, "return function(") ||
                StartsWith(trimmed, "return function ") ||
                trimmed.find("= function(") != std::string_view::npos ||
                trimmed.find("= function ") != std::string_view::npos) {
                open_controls.push_back({MissingControlCloseKind::kEnd,
                                         std::string(GetLuaLineIndent(lines[probe])),
                                         probe});
            }
        }

        if (open_controls.empty() || open_controls.front().line_index != line_index) {
            continue;
        }

        std::vector<std::string> close_before_yield;
        close_before_yield.reserve(open_controls.size() - 1);
        for (std::size_t i = open_controls.size(); i-- > 1;) {
            const MissingControlCloseFrame& frame = open_controls[i];
            close_before_yield.push_back(frame.indent +
                                         (frame.kind == MissingControlCloseKind::kUntil
                                              ? "until true"
                                              : "end"));
        }
        const MissingControlCloseFrame& outer_frame = open_controls.front();
        const std::string outer_close =
            outer_frame.indent +
            (outer_frame.kind == MissingControlCloseKind::kUntil ? "until true" : "end");

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + close_before_yield.size());
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(yield_line));
        candidate_lines.insert(candidate_lines.end(),
                               close_before_yield.begin(),
                               close_before_yield.end());
        candidate_lines.push_back(body_indent + "coroutine.yield()");
        candidate_lines.push_back(outer_close);

        std::size_t consumed_close_lines = 0;
        const std::size_t target_consume = close_before_yield.size() + 1;
        for (std::size_t probe = yield_line + 1; probe < tail_limit; ++probe) {
            const std::string_view raw_trimmed = TrimRawLuaLine(lines[probe]);
            if (raw_trimmed.empty()) {
                continue;
            }
            const bool closable_marker =
                IsSuppressedControlMarker(lines[probe]) ||
                raw_trimmed == "end" || raw_trimmed == "end,";
            if (closable_marker) {
                ++consumed_close_lines;
                continue;
            }
            candidate_lines.push_back(lines[probe]);
            consumed_close_lines = 0;
            break;
        }
        if (consumed_close_lines < target_consume) {
            continue;
        }

        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(tail_limit),
                               lines.end());
        lines = std::move(candidate_lines);
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_lifted_trailing_yield_across_close_run");
    }
    return true;
}

bool TryCollapseSuppressedElseYieldTailStateLoops(std::string& decompiled_source,
                                                  std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimLuaCodeLine(lines[line_index]) != "while true do") {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        const std::size_t outer_end = FindBalancedLuaBlockEnd(lines, line_index);
        std::size_t tail_limit = outer_end < lines.size() ? outer_end + 1 : lines.size();
        if (outer_end >= lines.size()) {
            for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
                const std::string_view raw_trimmed = TrimRawLuaLine(lines[probe]);
                if (raw_trimmed.empty() || IsSuppressedControlMarker(lines[probe])) {
                    continue;
                }
                if (GetLuaLineIndent(lines[probe]).size() <= outer_indent.size()) {
                    tail_limit = probe;
                    break;
                }
            }
        }
        if (tail_limit <= line_index + 4) {
            continue;
        }

        std::size_t else_line = lines.size();
        for (std::size_t probe = line_index + 1; probe < tail_limit; ++probe) {
            if (GetLuaLineIndent(lines[probe]) != body_indent) {
                continue;
            }
            if (TrimLuaCodeLine(lines[probe]) == "else") {
                else_line = probe;
            }
        }
        if (else_line >= lines.size()) {
            continue;
        }

        const std::size_t yield_line = FindPreviousNonCommentLuaLine(lines, else_line);
        const std::string yield_indent =
            yield_line < lines.size() ? GetLuaLineIndent(lines[yield_line]) : std::string();
        if (yield_line >= lines.size() || !yield_indent.starts_with(body_indent) ||
            !IsLuaYieldWaitLine(lines[yield_line])) {
            continue;
        }

        std::size_t tail_begin = yield_line;
        while (tail_begin > line_index + 1) {
            const std::size_t probe = FindPreviousNonCommentLuaLine(lines, tail_begin);
            if (probe >= lines.size() || !IsSuppressedControlMarker(lines[probe])) {
                break;
            }
            tail_begin = probe;
            if (probe == 0) {
                break;
            }
        }
        if (tail_begin == yield_line) {
            continue;
        }

        bool valid_tail = true;
        for (std::size_t probe = tail_begin; probe < yield_line; ++probe) {
            const std::string_view raw_trimmed = TrimRawLuaLine(lines[probe]);
            if (!raw_trimmed.empty() && !IsSuppressedControlMarker(lines[probe])) {
                valid_tail = false;
                break;
            }
        }
        bool saw_suppressed_close_after_else = false;
        for (std::size_t probe = else_line + 1; probe < tail_limit; ++probe) {
            const std::string_view raw_trimmed = TrimRawLuaLine(lines[probe]);
            if (raw_trimmed.empty()) {
                continue;
            }
            if (!IsSuppressedControlMarker(lines[probe]) &&
                raw_trimmed != "end" && raw_trimmed != "end,") {
                valid_tail = false;
                break;
            }
            saw_suppressed_close_after_else = true;
        }
        if (!valid_tail || !saw_suppressed_close_after_else) {
            continue;
        }

        std::vector<std::string> replacement = {
            body_indent + "end",
            body_indent + "coroutine.yield()",
            outer_indent + "end",
        };
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(tail_begin),
                    lines.begin() + static_cast<std::ptrdiff_t>(tail_limit));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(tail_begin),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_collapsed_suppressed_else_yield_tail");
    }
    return true;
}

bool TrySplitRepeatedWaitCollectionLoops(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string collection;
        std::string loop_variable;
        if (!ParseLuaIpairsLoop(lines[line_index], &collection, &loop_variable)) {
            continue;
        }

        const std::size_t outer_end = FindBalancedLuaBlockEnd(lines, line_index);
        if (outer_end >= lines.size() || outer_end <= line_index + 8 ||
            outer_end - line_index > 160) {
            continue;
        }

        const std::string outer_indent(GetLuaLineIndent(lines[line_index]));
        const std::string body_indent = outer_indent + "  ";

        std::size_t waits_found = 0;
        std::size_t second_wait_end = lines.size();
        for (std::size_t probe = line_index + 1; probe < outer_end; ++probe) {
            if (GetLuaLineIndent(lines[probe]) != body_indent) {
                continue;
            }

            std::string wait_condition;
            if (!ParseLuaWhileCondition(lines[probe], &wait_condition) ||
                wait_condition.find(loop_variable) == std::string::npos) {
                continue;
            }

            const std::size_t wait_end = FindBalancedLuaBlockEnd(lines, probe);
            if (wait_end >= outer_end) {
                break;
            }

            const std::size_t wait_body =
                FindFirstNonCommentLuaLineInRange(lines, probe + 1, wait_end);
            if (wait_body >= wait_end || TrimLuaCodeLine(lines[wait_body]) != "coroutine.yield()" ||
                FindLastNonCommentLuaLineInRange(lines, probe + 1, wait_end) != wait_body) {
                continue;
            }

            ++waits_found;
            if (waits_found == 2) {
                second_wait_end = wait_end;
                break;
            }
            probe = wait_end;
        }

        if (waits_found < 2 || second_wait_end >= outer_end) {
            continue;
        }

        const std::size_t split_start = FindNextNonCommentLuaLine(lines, second_wait_end + 1);
        if (split_start >= outer_end || GetLuaLineIndent(lines[split_start]) != body_indent) {
            continue;
        }

        std::string split_collection;
        std::string split_variable;
        if (!ParseLuaIpairsLoop(lines[split_start], &split_collection, &split_variable) ||
            split_collection != collection) {
            continue;
        }

        bool clear_gap = true;
        for (std::size_t probe = second_wait_end + 1; probe < split_start; ++probe) {
            if (!TrimLuaCodeLine(lines[probe]).empty()) {
                clear_gap = false;
                break;
            }
        }
        if (!clear_gap) {
            continue;
        }

        std::vector<std::string> replacement;
        replacement.reserve(outer_end - line_index + 2);
        replacement.insert(replacement.end(),
                           lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                           lines.begin() + static_cast<std::ptrdiff_t>(split_start));
        replacement.push_back(outer_indent + "end");
        AppendReindentedLuaBlock(lines,
                                 split_start,
                                 outer_end,
                                 body_indent,
                                 outer_indent,
                                 &replacement);

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(outer_end + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement.begin(),
                     replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_split_repeated_wait_collection_loop");
    }
    return true;
}

bool TrySimplifyRenderedWaitLoopPatterns(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    changed |= refii::lua::SimplifyRenderedLuaYieldElseWaitLoops(&lines);
    changed |= refii::lua::SimplifyRenderedLuaLoopGuardedYieldElse(&lines);
    changed |= refii::lua::SimplifyRenderedLuaDanglingOuterTrueWaitWrappers(&lines);
    changed |= refii::lua::SimplifyRenderedLuaDanglingElseGuardedWaitLoops(&lines);
    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_simplified_rendered_wait_loops");
    }
    return true;
}

bool TryRewriteDoReturnEndBranches(std::string& decompiled_source,
                                   std::string_view validation_error,
                                   std::string* note) {
    if (decompiled_source.find("do return end") == std::string_view::npos &&
        validation_error.find("near 'if'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos &&
        validation_error.find("near 'do'") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos &&
        validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near '<eof>'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    const auto indentation_of = [&](std::size_t index) -> std::string {
        return index < lines.size() ? GetLuaLineIndent(lines[index]) : std::string();
    };
    const auto parent_branch_indent_of = [&](std::size_t index) -> std::string {
        const std::string indent = indentation_of(index);
        if (indent.empty()) {
            return indent;
        }

        for (std::size_t probe = index; probe-- > 0;) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty()) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string probe_indent = indentation_of(probe);
            if (probe_indent.size() >= indent.size()) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ")) {
                return probe_indent;
            }
        }

        return DedentLuaIndent(indent);
    };
    const auto parent_branch_index_of = [&](std::size_t index) -> std::size_t {
        const std::string indent = indentation_of(index);
        if (indent.empty()) {
            return lines.size();
        }

        for (std::size_t probe = index; probe-- > 0;) {
            const std::string_view trimmed = TrimLuaCodeLine(lines[probe]);
            if (trimmed.empty()) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string probe_indent = indentation_of(probe);
            if (probe_indent.size() >= indent.size()) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ")) {
                return probe;
            }
        }

        return lines.size();
    };
    const auto strip_duplicate_end_after_branch_end = [&](std::size_t first_end) -> bool {
        if (first_end >= lines.size() || TrimWhitespace(lines[first_end]) != "end") {
            return false;
        }

        std::size_t second_end = first_end + 1;
        while (second_end < lines.size() && TrimWhitespace(lines[second_end]).empty()) {
            ++second_end;
        }
        if (second_end >= lines.size() || TrimWhitespace(lines[second_end]) != "end" ||
            indentation_of(second_end) != indentation_of(first_end)) {
            return false;
        }

        std::size_t continuation = second_end + 1;
        while (continuation < lines.size() &&
               (TrimWhitespace(lines[continuation]).empty() ||
                StartsWith(TrimWhitespace(lines[continuation]), "--"))) {
            ++continuation;
        }
        if (continuation >= lines.size()) {
            return false;
        }

        const std::string_view continuation_trimmed = TrimWhitespace(lines[continuation]);
        if (indentation_of(continuation).size() > indentation_of(first_end).size() ||
            continuation_trimmed == "end" || continuation_trimmed == "else" ||
            StartsWith(continuation_trimmed, "until ")) {
            return false;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(second_end));
        return true;
    };

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "do return end") {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size()) {
            continue;
        }

        const std::string indent = indentation_of(i);
        const std::string branch_indent = parent_branch_indent_of(i);
        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed == "end" || next_trimmed == "else" ||
            StartsWith(next_trimmed, "elseif ")) {
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
            changed = true;
            --i;
            continue;
        }

        if (StartsWith(next_trimmed, "if ") && EndsWith(next_trimmed, " then")) {
            const std::string next_branch_text(next_trimmed);
            const std::size_t previous = FindPreviousNonCommentLuaLine(lines, i);
            const std::string_view previous_trimmed =
                previous < lines.size() ? TrimWhitespace(lines[previous]) : std::string_view();
            const bool previous_closes_branch =
                previous < lines.size() &&
                (previous_trimmed == "end" || IsTerminalBranchStatement(previous_trimmed));
            if (!previous_closes_branch) {
                const std::size_t owner_branch = parent_branch_index_of(i);
                int nested_balance = 0;
                if (owner_branch < lines.size()) {
                    for (std::size_t probe = owner_branch + 1; probe < i; ++probe) {
                        nested_balance += ComputeLuaBlockDelta(lines[probe]);
                    }
                }
                if (nested_balance > 0) {
                    lines[i] = indent + "end";
                } else {
                    lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
                    --i;
                    next = i + 1;
                    while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
                        ++next;
                    }
                    if (next >= lines.size()) {
                        changed = true;
                        continue;
                    }
                }
                lines[next] = branch_indent + "elseif " + next_branch_text.substr(3);
                std::size_t lift_end = lines.size();
                for (std::size_t probe = next + 1; probe < lines.size(); ++probe) {
                    const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
                    if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                        continue;
                    }
                    if (indentation_of(probe).size() <= branch_indent.size()) {
                        lift_end = probe;
                        break;
                    }
                }
                for (std::size_t probe = next + 1; probe < lift_end; ++probe) {
                    const std::string current_indent = indentation_of(probe);
                    if (current_indent.size() <= branch_indent.size()) {
                        continue;
                    }
                    const std::string lifted_indent = DedentLuaIndent(current_indent);
                    lines[probe] = lifted_indent + lines[probe].substr(current_indent.size());
                }
                changed |= strip_duplicate_end_after_branch_end(lift_end);
                changed = true;
                continue;
            }
            lines[next] = branch_indent + "elseif " + next_branch_text.substr(3);
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
            changed = true;
            --i;
            continue;
        }

        lines[i] = branch_indent + "else";
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_do_return_branch");
    }
    return true;
}

bool TryStripStandaloneDoReturnEndArtifacts(std::string& decompiled_source,
                                            std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "do return end") {
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
        *note = JoinNotes(*note, "syntax_repaired_removed_do_return_end_artifact");
    }
    return true;
}

}  // namespace refii::lua::internal
