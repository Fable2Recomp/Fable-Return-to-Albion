#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua::internal {
namespace {

std::string ChooseUniqueLuaHelperName(const std::vector<std::string>& lines,
                                      std::string_view prefix) {
    for (std::size_t suffix = 1;; ++suffix) {
        const std::string candidate = std::string(prefix) + std::to_string(suffix);
        bool collides = false;
        for (const std::string& line : lines) {
            if (refii::lua::FindIdentifierUse(line, candidate) != std::string::npos) {
                collides = true;
                break;
            }
        }
        if (!collides) {
            return candidate;
        }
    }
}

bool ExtractInlineAnonymousFunctionCallSuffix(std::string_view trimmed,
                                              std::string* header_without_suffix,
                                              std::string* call_suffix) {
    if (header_without_suffix == nullptr || call_suffix == nullptr) {
        return false;
    }
    if (!StartsWith(trimmed, "function(") && !StartsWith(trimmed, "function (")) {
        return false;
    }

    const std::size_t open = trimmed.find('(');
    if (open == std::string_view::npos) {
        return false;
    }

    int paren_depth = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    bool escaped = false;
    std::size_t close = std::string_view::npos;
    for (std::size_t i = open; i < trimmed.size(); ++i) {
        const char ch = trimmed[i];
        if (escaped) {
            escaped = false;
            continue;
        }
        if ((in_single_quote || in_double_quote) && ch == '\\') {
            escaped = true;
            continue;
        }
        if (!in_double_quote && ch == '\'') {
            in_single_quote = !in_single_quote;
            continue;
        }
        if (!in_single_quote && ch == '"') {
            in_double_quote = !in_double_quote;
            continue;
        }
        if (in_single_quote || in_double_quote) {
            continue;
        }
        if (ch == '(') {
            ++paren_depth;
        } else if (ch == ')') {
            --paren_depth;
            if (paren_depth == 0) {
                close = i;
                break;
            }
        }
    }

    if (close == std::string_view::npos) {
        return false;
    }

    const std::string_view suffix = TrimWhitespace(trimmed.substr(close + 1));
    if (!IsBareLuaCallSuffix(suffix)) {
        return false;
    }

    *header_without_suffix = std::string(trimmed.substr(0, close + 1));
    *call_suffix = std::string(suffix);
    return true;
}

bool SplitTwoTopLevelLuaParts(std::string_view text,
                              std::string* first,
                              std::string* second) {
    if (first == nullptr || second == nullptr) {
        return false;
    }

    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    bool escaped = false;
    std::size_t split = std::string_view::npos;

    for (std::size_t i = 0; i < text.size(); ++i) {
        const char ch = text[i];
        if (escaped) {
            escaped = false;
            continue;
        }
        if ((in_single_quote || in_double_quote) && ch == '\\') {
            escaped = true;
            continue;
        }
        if (!in_double_quote && ch == '\'') {
            in_single_quote = !in_single_quote;
            continue;
        }
        if (!in_single_quote && ch == '"') {
            in_double_quote = !in_double_quote;
            continue;
        }
        if (in_single_quote || in_double_quote) {
            continue;
        }

        switch (ch) {
        case '(':
            ++paren_depth;
            break;
        case ')':
            --paren_depth;
            break;
        case '{':
            ++brace_depth;
            break;
        case '}':
            --brace_depth;
            break;
        case '[':
            ++bracket_depth;
            break;
        case ']':
            --bracket_depth;
            break;
        case ',':
            if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                if (split != std::string_view::npos) {
                    return false;
                }
                split = i;
            }
            break;
        default:
            break;
        }
    }

    if (split == std::string_view::npos) {
        return false;
    }

    *first = std::string(TrimWhitespace(text.substr(0, split)));
    *second = std::string(TrimWhitespace(text.substr(split + 1)));
    return !first->empty() && !second->empty();
}

bool ParseSplitMethodReferenceSeed(std::string_view line,
                                   std::string* function_temp,
                                   std::string* self_temp,
                                   std::string* receiver,
                                   std::string* method) {
    if (function_temp == nullptr || self_temp == nullptr || receiver == nullptr ||
        method == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    std::string lhs_first;
    std::string lhs_second;
    std::string rhs_first;
    std::string rhs_second;
    if (!SplitTwoTopLevelLuaParts(trimmed.substr(0, equal), &lhs_first, &lhs_second) ||
        !SplitTwoTopLevelLuaParts(trimmed.substr(equal + 1), &rhs_first, &rhs_second)) {
        return false;
    }

    if (!refii::lua::IsRenderTemporaryName(lhs_first) ||
        !refii::lua::IsRenderTemporaryName(lhs_second)) {
        return false;
    }

    const std::size_t colon = rhs_first.rfind(':');
    if (colon == std::string::npos || rhs_first.find('(', colon + 1) != std::string::npos) {
        return false;
    }

    const std::string parsed_receiver =
        std::string(TrimWhitespace(std::string_view(rhs_first).substr(0, colon)));
    const std::string parsed_method =
        std::string(TrimWhitespace(std::string_view(rhs_first).substr(colon + 1)));
    if (parsed_receiver.empty() || parsed_method.empty() || rhs_second != parsed_receiver ||
        !refii::lua::IsValidLuaIdentifier(parsed_method)) {
        return false;
    }

    *function_temp = std::move(lhs_first);
    *self_temp = std::move(lhs_second);
    *receiver = parsed_receiver;
    *method = parsed_method;
    return true;
}

bool IsAssignmentToIdentifier(std::string_view trimmed, std::string_view identifier) {
    std::string candidate_lhs;
    std::string candidate_rhs;
    if (!ParseSimpleRenderedAssignment(trimmed, nullptr, &candidate_lhs, &candidate_rhs)) {
        return false;
    }
    return TrimWhitespace(candidate_lhs) == identifier;
}

bool IsAssignmentToIdentifierField(std::string_view trimmed, std::string_view identifier) {
    std::string candidate_lhs;
    std::string candidate_rhs;
    if (!ParseSimpleRenderedAssignment(trimmed, nullptr, &candidate_lhs, &candidate_rhs)) {
        return false;
    }

    const std::string_view lhs = TrimWhitespace(candidate_lhs);
    if (lhs.size() <= identifier.size() || !lhs.starts_with(identifier)) {
        return false;
    }

    const char separator = lhs[identifier.size()];
    return separator == '.' || separator == '[';
}

}  // namespace

bool ParseSimpleRenderedAssignment(std::string_view line,
                                   std::string* indent_out,
                                   std::string* lhs_out,
                                   std::string* rhs_out) {
    if (lhs_out == nullptr || rhs_out == nullptr) {
        return false;
    }

    const std::size_t indent_count = line.find_first_not_of(" \t");
    if (indent_out != nullptr) {
        *indent_out = indent_count == std::string_view::npos
                          ? std::string()
                          : std::string(line.substr(0, indent_count));
    }
    std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    if (trimmed.empty()) {
        return false;
    }
    if (StartsWith(trimmed, "local ")) {
        trimmed = TrimWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimWhitespace(trimmed.substr(equal + 1));
    if (lhs.empty() || rhs.empty() || lhs.find(',') != std::string_view::npos) {
        return false;
    }

    *lhs_out = std::string(lhs);
    *rhs_out = std::string(rhs);
    return true;
}

bool TryRewriteImmediateAnonymousFunctionInvocations(std::string& decompiled_source,
                                                     std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        if (!StartsWith(trimmed, "function(") && !StartsWith(trimmed, "function (")) {
            continue;
        }

        const std::size_t function_end =
            refii::lua::FindRenderedFunctionBlockEnd(lines, line_index);
        if (function_end >= lines.size() || function_end < line_index) {
            continue;
        }

        std::string call_suffix;
        std::size_t suffix_line = std::string::npos;
        std::string header_without_suffix;
        if (!ExtractInlineAnonymousFunctionCallSuffix(
                trimmed, &header_without_suffix, &call_suffix)) {
            std::size_t probe = function_end + 1;
            while (probe < lines.size()) {
                const std::string_view probe_trimmed =
                    TrimWhitespace(StripLineComment(lines[probe]));
                if (probe_trimmed.empty()) {
                    ++probe;
                    continue;
                }
                if (!IsBareLuaCallSuffix(probe_trimmed)) {
                    break;
                }
                call_suffix = std::string(probe_trimmed);
                suffix_line = probe;
                header_without_suffix = std::string(trimmed);
                break;
            }
        }

        if (call_suffix.empty()) {
            continue;
        }

        const std::string indent = GetLuaLeadingIndent(lines[line_index]);
        const std::string helper_name =
            ChooseUniqueLuaHelperName(lines, "refii_immediate_call_helper_");
        lines[line_index] =
            indent + "local " + helper_name + " = " + header_without_suffix;
        if (suffix_line != std::string::npos) {
            lines[suffix_line] = indent + helper_name + call_suffix;
        } else {
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(function_end + 1),
                         indent + helper_name + call_suffix);
        }
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_immediate_anonymous_function_call");
    }
    return true;
}

bool TryNameOrphanTopLevelAnonymousFunctionCalls(std::string& decompiled_source,
                                                 std::string_view validation_error,
                                                 std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor =
        DescribeSyntaxError(validation_error);
    std::string best_candidate;
    std::string best_error;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[line_index]));
        if (!StartsWith(trimmed, "function(") && !StartsWith(trimmed, "function ")) {
            continue;
        }

        const std::string indent = GetLuaLeadingIndent(lines[line_index]);
        std::size_t suffix_line = lines.size();
        std::size_t insert_end_before = lines.size();
        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            const std::string_view probe_trimmed = TrimWhitespace(StripLineComment(lines[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (IsBareLuaCallSuffix(probe_trimmed) &&
                GetLuaLeadingIndent(lines[probe]) == indent) {
                suffix_line = probe;
                insert_end_before = probe;
                break;
            }
            if (GetLuaLeadingIndent(lines[probe]).size() <= indent.size() &&
                !IsSuppressedControlMarker(lines[probe]) &&
                probe_trimmed != "end") {
                break;
            }
        }

        if (suffix_line >= lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::string helper_name =
            ChooseUniqueLuaHelperName(candidate_lines, "refii_orphan_call_helper_");
        candidate_lines[line_index] = indent + "local " + helper_name + " = " +
                                      std::string(trimmed);

        std::size_t adjusted_suffix_line = suffix_line;
        std::size_t previous_significant = suffix_line;
        while (previous_significant > line_index + 1) {
            --previous_significant;
            const std::string_view previous_trimmed =
                TrimWhitespace(StripLineComment(candidate_lines[previous_significant]));
            if (previous_trimmed.empty()) {
                continue;
            }
            if (previous_trimmed == "end") {
                break;
            }
            candidate_lines.insert(
                candidate_lines.begin() + static_cast<std::ptrdiff_t>(insert_end_before),
                indent + "end");
            ++adjusted_suffix_line;
            break;
        }

        candidate_lines[adjusted_suffix_line] =
            indent + helper_name +
            std::string(
                TrimWhitespace(StripLineComment(candidate_lines[adjusted_suffix_line])));

        const std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = candidate;
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_named_orphan_top_level_anonymous_call");
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor =
            DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line ||
            !IsValidationOutcomeBetter(validation_error, candidate_error) ||
            (!best_error.empty() &&
             !IsValidationOutcomeBetter(best_error, candidate_error))) {
            continue;
        }

        best_candidate = candidate;
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_named_orphan_top_level_anonymous_call_progress");
    }
    return true;
}

bool TryRewriteAnonymousFunctionCallThroughNamedLocal(std::string& decompiled_source,
                                                      std::string_view validation_error,
                                                      std::string* note) {
    if (validation_error.find("expected near '('") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 3) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    std::string best_candidate;
    std::string best_error;
    for (std::size_t call_line = 2; call_line < lines.size(); ++call_line) {
        const std::string_view call_trimmed = TrimWhitespace(StripLineComment(lines[call_line]));
        std::string header_without_suffix;
        std::string call_suffix;
        if (!ExtractInlineAnonymousFunctionCallSuffix(
                call_trimmed, &header_without_suffix, &call_suffix)) {
            continue;
        }

        const std::size_t end_line = FindPreviousNonEmptyLuaLine(lines, call_line);
        if (end_line >= lines.size() ||
            TrimWhitespace(StripLineComment(lines[end_line])) != "end") {
            continue;
        }

        const std::size_t assignment_line = FindPreviousNonEmptyLuaLine(lines, end_line);
        if (assignment_line >= lines.size()) {
            continue;
        }

        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment(lines[assignment_line], &indent, &lhs, &rhs) ||
            TrimWhitespace(rhs) != header_without_suffix) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[call_line] = GetLuaLeadingIndent(lines[call_line]) + lhs + call_suffix;
        const std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = candidate;
            if (note != nullptr) {
                *note = JoinNotes(
                    *note, "syntax_repaired_named_local_anonymous_call_restored");
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor =
            DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line ||
            !IsValidationOutcomeBetter(validation_error, candidate_error) ||
            (!best_error.empty() &&
             !IsValidationOutcomeBetter(best_error, candidate_error))) {
            continue;
        }

        best_candidate = candidate;
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note, "syntax_repaired_named_local_anonymous_call_restored_progress");
    }
    return true;
}

bool TryRestoreStrandedTopLevelHelperFunctionBlocks(std::string& decompiled_source,
                                                    std::string_view validation_error,
                                                    std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 6) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor =
        DescribeSyntaxError(validation_error);
    std::string best_candidate;
    std::string best_error;
    for (std::size_t anchor_line = 0; anchor_line < lines.size(); ++anchor_line) {
        const std::string_view anchor_trimmed = TrimWhitespace(StripLineComment(lines[anchor_line]));
        if (anchor_trimmed.empty()) {
            continue;
        }

        const std::string anchor_indent = GetLuaLeadingIndent(lines[anchor_line]);
        const std::size_t body_start = FindNextSignificantLuaLine(lines, anchor_line + 1);
        if (body_start >= lines.size()) {
            continue;
        }
        const std::string body_indent = GetLuaLeadingIndent(lines[body_start]);
        if (body_indent.size() <= anchor_indent.size()) {
            continue;
        }

        std::size_t body_end = lines.size();
        for (std::size_t probe = body_start; probe < lines.size(); ++probe) {
            const std::string_view probe_trimmed = TrimWhitespace(StripLineComment(lines[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }

            const std::string probe_indent = GetLuaLeadingIndent(lines[probe]);
            if (probe_indent.size() == anchor_indent.size() && probe_trimmed == "end") {
                body_end = probe;
                break;
            }
            if (probe != body_start && probe_indent.size() <= anchor_indent.size() &&
                LooksLikeTopLevelBoundary(probe_trimmed)) {
                break;
            }
        }
        if (body_end >= lines.size() || body_end <= body_start) {
            continue;
        }

        std::string helper_header;
        std::vector<std::pair<std::size_t, std::string>> helper_calls;
        for (std::size_t probe = body_end + 1; probe < lines.size(); ++probe) {
            const std::string_view probe_trimmed = TrimWhitespace(StripLineComment(lines[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }

            const std::string probe_indent = GetLuaLeadingIndent(lines[probe]);
            if (probe_indent.size() != anchor_indent.size()) {
                continue;
            }

            std::string parsed_header;
            std::string call_suffix;
            if (ExtractInlineAnonymousFunctionCallSuffix(
                    probe_trimmed, &parsed_header, &call_suffix)) {
                if (helper_header.empty()) {
                    helper_header = std::move(parsed_header);
                } else if (helper_header != parsed_header) {
                    continue;
                }
                helper_calls.emplace_back(probe, std::move(call_suffix));
                continue;
            }
        }

        if (helper_calls.size() < 2 || helper_header.empty()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::string helper_name =
            ChooseUniqueLuaHelperName(candidate_lines, "refii_top_level_helper_");
        const std::string helper_prefix =
            anchor_indent + "local " + helper_name + " = ";
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(body_start),
                               helper_prefix + helper_header);
        for (const auto& [line_index, call_suffix] : helper_calls) {
            const std::size_t adjusted_index = line_index + 1;
            candidate_lines[adjusted_index] = anchor_indent + helper_name + call_suffix;
        }

        const std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = candidate;
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_restored_stranded_top_level_helper_function");
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor =
            DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line ||
            !IsValidationOutcomeBetter(validation_error, candidate_error) ||
            (!best_error.empty() &&
             !IsValidationOutcomeBetter(best_error, candidate_error))) {
            continue;
        }

        best_candidate = candidate;
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_restored_stranded_top_level_helper_function_progress");
    }
    return true;
}

bool TryRewriteBareMethodReferenceConstructorAssignments(std::string& decompiled_source,
                                                         std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment(lines[i], &indent, &lhs, &rhs)) {
            continue;
        }

        lhs = std::string(TrimWhitespace(lhs));
        rhs = std::string(TrimWhitespace(rhs));
        if (lhs.empty() || rhs.empty() || !refii::lua::IsRenderTemporaryName(lhs)) {
            continue;
        }

        const std::size_t colon = rhs.rfind(':');
        if (colon == std::string::npos || rhs.find('(', colon + 1) != std::string::npos) {
            continue;
        }

        const std::string_view receiver = TrimWhitespace(std::string_view(rhs).substr(0, colon));
        const std::string_view method = TrimWhitespace(std::string_view(rhs).substr(colon + 1));
        if (receiver.empty() || method.empty() || receiver.find_first_of(" =,") != std::string::npos ||
            !refii::lua::IsValidLuaIdentifier(method)) {
            continue;
        }

        bool saw_field_assignment = false;
        bool blocked = false;
        std::size_t significant_lines = 0;
        for (std::size_t probe = i + 1; probe < lines.size() && significant_lines < 12; ++probe) {
            const std::string_view probe_trimmed =
                TrimWhitespace(StripLineComment(lines[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            ++significant_lines;

            if (probe_trimmed == "end" || probe_trimmed == "else" ||
                StartsWith(probe_trimmed, "elseif ") || probe_trimmed == "return" ||
                StartsWith(probe_trimmed, "return ") || StartsWith(probe_trimmed, "if ") ||
                StartsWith(probe_trimmed, "for ") || StartsWith(probe_trimmed, "while ") ||
                probe_trimmed == "repeat" || StartsWith(probe_trimmed, "until ")) {
                break;
            }

            if (IsAssignmentToIdentifier(probe_trimmed, lhs) ||
                ContainsWord(probe_trimmed, lhs + "(")) {
                blocked = true;
                break;
            }

            if (IsAssignmentToIdentifierField(probe_trimmed, lhs)) {
                saw_field_assignment = true;
                continue;
            }

            if (ContainsWord(probe_trimmed, lhs) && !ContainsWord(probe_trimmed, lhs + ".")) {
                blocked = true;
                break;
            }
        }

        if (!saw_field_assignment || blocked) {
            continue;
        }

        lines[i] = indent + lhs + " = " + rhs + "()";
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_bare_method_constructor_call");
    }
    return true;
}

bool TryRewriteSplitMethodReferenceSelfCalls(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        std::string function_temp;
        std::string self_temp;
        std::string receiver;
        std::string method;
        if (!ParseSplitMethodReferenceSeed(
                lines[i], &function_temp, &self_temp, &receiver, &method)) {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(StripLineComment(lines[next])).empty()) {
            ++next;
        }
        if (next >= lines.size()) {
            continue;
        }

        const std::string_view stripped = StripLineComment(lines[next]);
        const std::string_view comment =
            stripped.size() < lines[next].size()
                ? std::string_view(lines[next]).substr(stripped.size())
                : std::string_view();

        std::string indent;
        std::string assignment_lhs;
        std::string assignment_rhs;
        std::string call_expression;
        std::string rewritten_prefix;
        if (ParseSimpleRenderedAssignment(lines[next], &indent, &assignment_lhs, &assignment_rhs)) {
            call_expression = std::string(TrimWhitespace(assignment_rhs));
            rewritten_prefix =
                indent + std::string(TrimWhitespace(assignment_lhs)) + " = ";
        } else {
            indent = GetLuaLineIndent(lines[next]);
            call_expression = std::string(TrimWhitespace(stripped));
            rewritten_prefix = indent;
        }

        const std::string call_prefix = function_temp + "(";
        if (!StartsWith(call_expression, call_prefix) || !EndsWith(call_expression, ")")) {
            continue;
        }

        std::string arguments =
            call_expression.substr(call_prefix.size(),
                                   call_expression.size() - call_prefix.size() - 1);
        arguments = std::string(TrimWhitespace(arguments));
        std::string remaining_arguments;
        if (arguments == self_temp) {
            remaining_arguments.clear();
        } else if (StartsWith(arguments, self_temp + ",")) {
            remaining_arguments =
                std::string(TrimWhitespace(arguments.substr(self_temp.size() + 1)));
        } else {
            continue;
        }

        std::string rewritten = rewritten_prefix + receiver + ":" + method + "(";
        if (!remaining_arguments.empty()) {
            rewritten += remaining_arguments;
        }
        rewritten += ")";
        rewritten += std::string(comment);

        lines[next] = std::move(rewritten);
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
        *note = JoinNotes(*note, "syntax_repaired_split_method_reference_self_call");
    }
    return true;
}

bool TryMergeSplitLocalMultiResultAssignments(std::string& decompiled_source,
                                              std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[i]));

        std::vector<std::string> local_names;
        if (!StartsWith(trimmed, "local ") || trimmed.find('=') != std::string_view::npos) {
            continue;
        }

        std::string_view name_list = TrimWhitespace(trimmed.substr(6));
        if (name_list.empty()) {
            continue;
        }

        std::size_t part_begin = 0;
        bool valid_names = true;
        while (part_begin < name_list.size()) {
            const std::size_t comma = name_list.find(',', part_begin);
            const std::string_view part =
                TrimWhitespace(name_list.substr(part_begin, comma - part_begin));
            if (part.empty() || !refii::lua::IsValidLuaIdentifier(part)) {
                valid_names = false;
                break;
            }
            local_names.emplace_back(part);
            if (comma == std::string_view::npos) {
                break;
            }
            part_begin = comma + 1;
        }

        if (!valid_names || local_names.size() < 2) {
            continue;
        }

        const std::size_t assign_first_index = FindNextSignificantLuaLine(lines, i + 1);
        const std::size_t assign_second_index =
            FindNextSignificantLuaLine(lines, assign_first_index + 1);
        if (assign_first_index >= lines.size() || assign_second_index >= lines.size()) {
            continue;
        }

        std::string first_lhs;
        std::string first_rhs;
        if (!ParseSimpleRenderedAssignment(
                lines[assign_first_index], nullptr, &first_lhs, &first_rhs) ||
            TrimWhitespace(first_lhs) != local_names[0] || TrimWhitespace(first_rhs).empty()) {
            continue;
        }

        const std::string_view second_trimmed =
            TrimWhitespace(StripLineComment(lines[assign_second_index]));
        if (!StartsWith(second_trimmed, local_names[1]) ||
            second_trimmed.find('=') == std::string_view::npos ||
            TrimWhitespace(second_trimmed.substr(second_trimmed.find('=') + 1)).size() != 0) {
            continue;
        }

        const std::string indent = GetLuaLeadingIndent(lines[i]);
        lines[i] = indent + "local " + local_names[0] + ", " + local_names[1] + " = " +
                   std::string(TrimWhitespace(first_rhs));
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(assign_second_index));
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(assign_first_index));
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
        *note = JoinNotes(*note, "syntax_repaired_split_local_multi_result");
    }
    return true;
}

bool TryRewriteBareMethodReferenceAssignments(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        const std::size_t comment = line.find("--");
        const std::size_t limit = comment == std::string::npos ? line.size() : comment;
        const std::size_t equal = line.find('=');
        if (equal == std::string::npos || equal >= limit) {
            continue;
        }

        const std::size_t colon = line.find(':', equal + 1);
        if (colon == std::string::npos || colon >= limit) {
            continue;
        }

        const std::size_t paren = line.find('(', colon + 1);
        if (paren != std::string::npos && paren < limit) {
            continue;
        }

        line[colon] = '.';
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_bare_method_reference");
    }
    return true;
}

bool TryRewriteInvalidMethodFieldAssignments(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        const std::size_t comment = line.find("--");
        const std::size_t limit = comment == std::string::npos ? line.size() : comment;
        const std::size_t equal = line.find('=');
        if (equal == std::string::npos || equal >= limit) {
            continue;
        }

        const std::size_t colon = line.find(':');
        const std::size_t dot = line.find('.', colon == std::string::npos ? 0 : colon + 1);
        if (colon == std::string::npos || colon >= equal || dot == std::string::npos ||
            dot >= equal) {
            continue;
        }

        line[colon] = '.';
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_method_field_assignment");
    }
    return true;
}

}  // namespace refii::lua::internal
