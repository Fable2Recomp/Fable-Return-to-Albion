#include "tools/decompiler/rendered/rendered_readability.h"

#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <cctype>
#include <string>
#include <unordered_set>
#include <vector>

namespace refii::lua::internal {

namespace {

std::string StripQuotedStringLiterals(std::string_view line) {
    std::string stripped;
    stripped.reserve(line.size());

    char quote = '\0';
    bool escaped = false;
    for (char ch : line) {
        if (quote != '\0') {
            if (escaped) {
                escaped = false;
                stripped.push_back(' ');
                continue;
            }
            if (ch == '\\') {
                escaped = true;
                stripped.push_back(' ');
                continue;
            }
            if (ch == quote) {
                quote = '\0';
                stripped.push_back(' ');
                continue;
            }
            stripped.push_back(' ');
            continue;
        }

        if (ch == '"' || ch == '\'') {
            quote = ch;
            stripped.push_back(' ');
            continue;
        }

        stripped.push_back(ch);
    }

    return stripped;
}

bool IsSimpleLuaIdentityTokenChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_' || ch == '.' || ch == ':' || ch == '[' ||
           ch == ']';
}

bool IsSimpleLuaIdentityToken(std::string_view token) {
    if (token.empty()) {
        return false;
    }
    bool has_identifier_head = false;
    for (char ch : token) {
        if (!IsSimpleLuaIdentityTokenChar(ch)) {
            return false;
        }
        if (std::isalpha(static_cast<unsigned char>(ch)) != 0 || ch == '_') {
            has_identifier_head = true;
        }
    }
    return has_identifier_head;
}

bool TryNormalizeDisassemblyRecoveryInvalidNilArithmeticLine(std::string* line) {
    if (line == nullptr || line->empty()) {
        return false;
    }

    bool changed = false;
    auto normalize_postfix_pattern = [&](std::string_view pattern) {
        std::size_t search_from = 0;
        while (search_from < line->size()) {
            const std::size_t op_pos = line->find(pattern, search_from);
            if (op_pos == std::string::npos) {
                break;
            }
            std::size_t lhs_end = op_pos;
            while (lhs_end > 0 &&
                   std::isspace(static_cast<unsigned char>((*line)[lhs_end - 1])) != 0) {
                --lhs_end;
            }
            std::size_t lhs_start = lhs_end;
            while (lhs_start > 0 && IsSimpleLuaIdentityTokenChar((*line)[lhs_start - 1])) {
                --lhs_start;
            }
            const std::string_view lhs(line->data() + lhs_start, lhs_end - lhs_start);
            if (!IsSimpleLuaIdentityToken(lhs)) {
                search_from = op_pos + pattern.size();
                continue;
            }

            const std::size_t erase_begin = lhs_end;
            const std::size_t erase_end = op_pos + pattern.size();
            line->erase(erase_begin, erase_end - erase_begin);
            search_from = lhs_start + lhs.size();
            changed = true;
        }
    };

    normalize_postfix_pattern("+ nil");
    normalize_postfix_pattern("- nil");
    normalize_postfix_pattern("* nil");
    normalize_postfix_pattern("/ nil");

    auto normalize_prefix_pattern = [&](std::string_view pattern) {
        std::size_t search_from = 0;
        while (search_from < line->size()) {
            const std::size_t nil_pos = line->find(pattern, search_from);
            if (nil_pos == std::string::npos) {
                break;
            }
            std::size_t rhs_start = nil_pos + pattern.size();
            while (rhs_start < line->size() &&
                   std::isspace(static_cast<unsigned char>((*line)[rhs_start])) != 0) {
                ++rhs_start;
            }
            std::size_t rhs_end = rhs_start;
            while (rhs_end < line->size() && IsSimpleLuaIdentityTokenChar((*line)[rhs_end])) {
                ++rhs_end;
            }
            const std::string_view rhs(line->data() + rhs_start, rhs_end - rhs_start);
            if (!IsSimpleLuaIdentityToken(rhs)) {
                search_from = nil_pos + pattern.size();
                continue;
            }
            line->erase(nil_pos, rhs_start - nil_pos);
            search_from = nil_pos + rhs.size();
            changed = true;
        }
    };

    normalize_prefix_pattern("nil +");
    normalize_prefix_pattern("nil -");
    normalize_prefix_pattern("nil *");
    normalize_prefix_pattern("nil /");

    auto normalize_nil_math_intrinsic = [&](std::string_view pattern) {
        std::size_t search_from = 0;
        while (search_from < line->size()) {
            const std::size_t pos = line->find(pattern, search_from);
            if (pos == std::string::npos) {
                break;
            }
            line->erase(pos + std::string("math.max(").size(), std::string("nil,").size());
            search_from = pos + std::string("math.max(").size();
            changed = true;
        }
    };

    normalize_nil_math_intrinsic("math.max(nil,");
    normalize_nil_math_intrinsic("math.min(nil,");

    const auto replace_all = [&](std::string_view needle,
                                 std::string_view replacement) {
        std::size_t search_from = 0;
        while (search_from < line->size()) {
            const std::size_t pos = line->find(needle, search_from);
            if (pos == std::string::npos) {
                break;
            }
            line->replace(pos, needle.size(), replacement);
            search_from = pos + replacement.size();
            changed = true;
        }
    };

    replace_all("* nil", "* 0");
    replace_all("nil *", "0 *");
    replace_all("/ nil", "/ 1");
    replace_all("nil /", "0 /");

    return changed;
}

}  // namespace

bool LooksLikeRegisterizedTemporaryName(std::string_view name) {
    if (name.empty()) {
        return false;
    }

    if (name.front() != 'r' || name.size() <= 1) {
        return false;
    }

    return std::all_of(name.begin() + 1, name.end(), [](char ch) {
        return std::isdigit(static_cast<unsigned char>(ch)) != 0;
    });
}

bool LooksLikeTemporarySelfReferenceAssignment(std::string_view trimmed) {
    const std::string_view lhs = ExtractLeadingIdentifier(trimmed);
    if (!LooksLikeRegisterizedTemporaryName(lhs)) {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string::npos) {
        return false;
    }

    const std::string_view rhs = TrimWhitespace(trimmed.substr(equal + 1));
    if (!StartsWith(rhs, lhs)) {
        return false;
    }
    if (rhs.size() == lhs.size()) {
        return false;
    }

    const char next = rhs[lhs.size()];
    return next == '(' || next == '[' || next == '.' || next == ':';
}

bool LooksLikeTemporaryTableFieldAssignment(std::string_view trimmed) {
    const std::string_view lhs = ExtractLeadingIdentifier(trimmed);
    if (!LooksLikeRegisterizedTemporaryName(lhs)) {
        return false;
    }

    if (trimmed.size() <= lhs.size()) {
        return false;
    }

    const char next = trimmed[lhs.size()];
    if (next != '[' && next != '.') {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    return equal != std::string::npos && equal > lhs.size();
}

LuaReadabilitySummary SummarizeLuaReadability(std::string_view source) {
    LuaReadabilitySummary summary;
    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(source, &had_trailing_newline);
    std::unordered_set<std::string> unique_temporaries;

    for (const std::string& raw_line : lines) {
        const std::string_view stripped = StripLineComment(raw_line);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        ++summary.nonempty_line_count;

        std::size_t temporary_tokens_on_line = 0;
        ForEachLuaIdentifierToken(
            stripped,
            LuaTokenScanOptions{},
            [&](std::string_view token, std::size_t, std::size_t) {
                if (!LooksLikeRegisterizedTemporaryName(token)) {
                    return;
                }
                ++summary.temporary_identifier_count;
                ++temporary_tokens_on_line;
                unique_temporaries.emplace(token);
            });

        if (temporary_tokens_on_line >= 2) {
            ++summary.multi_temporary_token_line_count;
        }

        const std::string_view local_name = ExtractLocalDeclarationName(trimmed);
        if (!local_name.empty() && LooksLikeRegisterizedTemporaryName(local_name)) {
            ++summary.temporary_local_declaration_count;
            ++summary.temporary_leading_line_count;
        } else {
            const std::string_view leading = ExtractLeadingIdentifier(trimmed);
            if (!leading.empty() && LooksLikeRegisterizedTemporaryName(leading)) {
                ++summary.temporary_leading_line_count;
                if (trimmed.find('=') != std::string_view::npos) {
                    ++summary.temporary_assignment_count;
                }
                if (LooksLikeTemporaryTableFieldAssignment(trimmed)) {
                    ++summary.temporary_table_field_assignment_count;
                }
            }
        }

        if (LooksLikeTemporarySelfReferenceAssignment(trimmed)) {
            ++summary.temporary_self_reference_assignment_count;
        }
    }

    summary.unique_temporary_identifier_count = unique_temporaries.size();
    return summary;
}

std::size_t CountLikelyUndefinedTemporaryUses(std::string_view source) {
    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(source, &had_trailing_newline);
    std::unordered_set<std::string> seen_temporary_definitions;
    std::unordered_set<std::string> undefined_use_keys;

    const auto collect_simple_identifier_list =
        [](std::string_view text, std::unordered_set<std::string>* names_out) {
            if (names_out == nullptr) {
                return;
            }

            text = TrimWhitespace(text);
            if (text.empty()) {
                return;
            }

            std::size_t cursor = 0;
            while (cursor < text.size()) {
                const std::size_t comma = text.find(',', cursor);
                std::string_view part =
                    comma == std::string_view::npos ? text.substr(cursor)
                                                    : text.substr(cursor, comma - cursor);
                part = TrimWhitespace(part);
                if (!part.empty()) {
                    const std::string_view leading = ExtractLeadingIdentifier(part);
                    if (!leading.empty() &&
                        LooksLikeRegisterizedTemporaryName(leading)) {
                        names_out->insert(std::string(leading));
                    }
                }
                if (comma == std::string_view::npos) {
                    break;
                }
                cursor = comma + 1;
            }
        };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        std::unordered_set<std::string> definitions_on_line;

        if (StartsWith(trimmed, "local ")) {
            const std::string_view local_rest = TrimWhitespace(trimmed.substr(6));
            const std::size_t equal = local_rest.find('=');
            const std::string_view declaration_names =
                equal == std::string_view::npos ? local_rest : local_rest.substr(0, equal);
            collect_simple_identifier_list(declaration_names, &definitions_on_line);
        }

        const std::size_t equal = trimmed.find('=');
        if (equal != std::string_view::npos) {
            collect_simple_identifier_list(trimmed.substr(0, equal), &definitions_on_line);
        }

        const std::size_t function_pos = trimmed.find("function");
        if (function_pos != std::string_view::npos) {
            const std::size_t params_open = trimmed.find('(', function_pos);
            const std::size_t params_close =
                params_open == std::string_view::npos
                    ? std::string_view::npos
                    : trimmed.find(')', params_open + 1);
            if (params_open != std::string_view::npos &&
                params_close != std::string_view::npos &&
                params_close > params_open + 1) {
                collect_simple_identifier_list(
                    trimmed.substr(params_open + 1, params_close - params_open - 1),
                    &definitions_on_line);
            }
        }

        ForEachLuaIdentifierToken(
            stripped,
            LuaTokenScanOptions{},
            [&](std::string_view token, std::size_t, std::size_t) {
                if (!LooksLikeRegisterizedTemporaryName(token) ||
                    definitions_on_line.contains(std::string(token)) ||
                    seen_temporary_definitions.contains(std::string(token))) {
                    return;
                }
                undefined_use_keys.insert(std::to_string(line_index) + ":" +
                                          std::string(token));
            });

        seen_temporary_definitions.insert(definitions_on_line.begin(),
                                          definitions_on_line.end());
    }

    return undefined_use_keys.size();
}

std::size_t ScoreLuaReadabilityRisk(const LuaReadabilitySummary& summary) {
    return summary.temporary_leading_line_count * 5 +
           summary.temporary_local_declaration_count * 4 +
           summary.temporary_assignment_count * 3 +
           summary.temporary_self_reference_assignment_count * 8 +
           summary.multi_temporary_token_line_count * 2 +
           summary.unique_temporary_identifier_count;
}

bool LooksLikeRegisterizedLua(const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 10) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count >= 3 &&
        summary.temporary_leading_line_count * 2 >= summary.nonempty_line_count) {
        return true;
    }

    if (summary.temporary_leading_line_count >= 8 &&
        summary.multi_temporary_token_line_count >= 6 &&
        ScoreLuaReadabilityRisk(summary) >= 48) {
        return true;
    }

    return summary.nonempty_line_count >= 24 &&
           summary.temporary_leading_line_count * 3 >= summary.nonempty_line_count &&
           summary.unique_temporary_identifier_count >= 4 &&
           ScoreLuaReadabilityRisk(summary) >= 64;
}

bool ContainsNilArithmeticPattern(std::string_view line) {
    return line.find("* nil") != std::string_view::npos ||
           line.find("nil *") != std::string_view::npos ||
           line.find("/ nil") != std::string_view::npos ||
           line.find("nil /") != std::string_view::npos ||
           line.find("+ nil") != std::string_view::npos ||
           line.find("nil +") != std::string_view::npos ||
           line.find("- nil") != std::string_view::npos ||
           line.find("nil -") != std::string_view::npos;
}

std::size_t CountObviouslyInvalidNilUsage(std::string_view source) {
    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(source, &had_trailing_newline);
    std::size_t count = 0;
    for (const std::string& line : lines) {
        const std::string stripped_literals =
            StripQuotedStringLiterals(StripLineComment(line));
        const std::string_view stripped(stripped_literals);
        if (stripped.find("nil[") != std::string_view::npos ||
            stripped.find("nil(") != std::string_view::npos ||
            stripped.find("nil.") != std::string_view::npos ||
            stripped.find("nil:") != std::string_view::npos ||
            stripped.find("math.max(nil") != std::string_view::npos ||
            stripped.find("math.min(nil") != std::string_view::npos ||
            ContainsNilArithmeticPattern(stripped)) {
            ++count;
        }
    }
    return count;
}

bool ContainsObviouslyInvalidNilUsage(std::string_view source) {
    return CountObviouslyInvalidNilUsage(source) != 0;
}

bool IsSimpleLuaIdentityToken(std::string_view token) {
    if (token.empty()) {
        return false;
    }
    bool has_identifier_head = false;
    for (char ch : token) {
        if (!IsSimpleLuaIdentityTokenChar(ch)) {
            return false;
        }
        if (std::isalpha(static_cast<unsigned char>(ch)) != 0 || ch == '_') {
            has_identifier_head = true;
        }
    }
    return has_identifier_head;
}

bool TryNormalizeDisassemblyRecoveryInvalidNilArithmetic(std::string& source,
                                                         std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(source, &had_trailing_newline);
    bool changed = false;
    for (std::string& line : lines) {
        changed |= TryNormalizeDisassemblyRecoveryInvalidNilArithmeticLine(&line);
    }
    if (!changed) {
        return false;
    }
    source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_disassembly_priority_nil_arithmetic_normalized");
    }
    return true;
}

bool IsRunnableLuaDespiteTemporaryNoise(const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 200) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    return summary.temporary_leading_line_count * 5 < summary.nonempty_line_count &&
           summary.multi_temporary_token_line_count * 6 < summary.nonempty_line_count &&
           summary.temporary_local_declaration_count * 16 < summary.nonempty_line_count;
}

bool IsStructuredTableInitializerLuaDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 80) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    if (summary.temporary_table_field_assignment_count < 24) {
        return false;
    }

    if (summary.temporary_local_declaration_count * 24 >= summary.nonempty_line_count) {
        return false;
    }

    if (summary.multi_temporary_token_line_count * 4 >= summary.nonempty_line_count) {
        return false;
    }

    if (summary.temporary_table_field_assignment_count * 2 >=
        summary.temporary_leading_line_count) {
        return true;
    }

    return summary.nonempty_line_count >= 300 &&
           summary.temporary_assignment_count * 2 >=
               summary.temporary_leading_line_count &&
           summary.unique_temporary_identifier_count <= 12;
}

bool IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 240) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    if (summary.unique_temporary_identifier_count > 24) {
        return false;
    }

    if (summary.temporary_leading_line_count * 8 >= summary.nonempty_line_count * 5) {
        return false;
    }

    if (summary.multi_temporary_token_line_count * 6 >= summary.nonempty_line_count) {
        return false;
    }

    const bool locals_heavy =
        summary.temporary_local_declaration_count >= 24 &&
        summary.temporary_local_declaration_count * 10 < summary.nonempty_line_count;
    const bool table_heavy = summary.temporary_table_field_assignment_count >= 20;
    const bool assignment_heavy = summary.temporary_assignment_count >= 48;

    return (locals_heavy || table_heavy) && assignment_heavy;
}

bool IsConservativeSmallHoldoutQualityRelaxCandidate(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 120) {
        return false;
    }
    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }
    if (summary.multi_temporary_token_line_count * 4 >= summary.nonempty_line_count) {
        return false;
    }
    return ScoreLuaReadabilityRisk(summary) <= 700;
}

bool IsConservativeGamefaceQualityRelaxCandidate(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 48) {
        return false;
    }
    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }
    if (summary.unique_temporary_identifier_count > 32) {
        return false;
    }
    if (summary.multi_temporary_token_line_count * 8 >=
        summary.nonempty_line_count * 7) {
        return false;
    }
    return ScoreLuaReadabilityRisk(summary) <= 2200;
}

bool IsConservativeBehaviorTerminalQualityRelaxCandidate(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 32 || summary.nonempty_line_count > 220) {
        return false;
    }
    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }
    if (summary.temporary_local_declaration_count > 16) {
        return false;
    }
    if (summary.multi_temporary_token_line_count * 3 >= summary.nonempty_line_count) {
        return false;
    }
    if (summary.temporary_leading_line_count * 2 >= summary.nonempty_line_count) {
        return false;
    }
    return ScoreLuaReadabilityRisk(summary) <= 240;
}

std::string DescribeLuaReadability(const LuaReadabilitySummary& summary) {
    return "lines=" + std::to_string(summary.nonempty_line_count) +
           ",temp_ids=" + std::to_string(summary.temporary_identifier_count) +
           ",temp_names=" + std::to_string(summary.unique_temporary_identifier_count) +
           ",temp_leading=" + std::to_string(summary.temporary_leading_line_count) +
           ",temp_locals=" + std::to_string(summary.temporary_local_declaration_count) +
           ",temp_assignments=" + std::to_string(summary.temporary_assignment_count) +
           ",temp_table_fields=" +
           std::to_string(summary.temporary_table_field_assignment_count) +
           ",temp_self_assignments=" +
           std::to_string(summary.temporary_self_reference_assignment_count) +
           ",temp_dense_lines=" +
           std::to_string(summary.multi_temporary_token_line_count) +
           ",risk=" + std::to_string(ScoreLuaReadabilityRisk(summary));
}

}  // namespace refii::lua::internal
