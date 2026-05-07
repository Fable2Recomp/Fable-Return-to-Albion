#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cctype>
#include <cstddef>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace refii::lua {
namespace {

template <typename PassFn>
bool RunTableTempPlaceholderCleanupPassLoop(int max_iterations, PassFn&& run_single_pass) {
    bool changed = false;
    for (int pass = 0; pass < max_iterations; ++pass) {
        const bool pass_changed = run_single_pass();
        if (!pass_changed) {
            break;
        }
        changed = true;
    }
    return changed;
}

}  // namespace

bool SimplifyRenderedLuaTempKeyedTableLiteralAliases(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct ResolvedRenderedTempExpression {
        std::string expression;
        std::size_t start_line = 0;
    };

    const auto extract_temporary_base = [](std::string_view expression) -> std::string {
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        if (!IsRenderedSafePrefixExpression(normalized)) {
            return {};
        }

        const std::size_t boundary = normalized.find_first_of(".:[(");
        const std::string_view base =
            boundary == std::string::npos ? std::string_view(normalized)
                                          : std::string_view(normalized).substr(0, boundary);
        return IsRenderTemporaryName(base) ? std::string(base) : std::string();
    };

    const auto rebuild_literal_line = [](std::string_view original_line,
                                         bool is_local,
                                         std::string_view lhs,
                                         const std::vector<std::string>& entries) {
        const std::string_view stripped = StripLuaLineComment(original_line);
        const std::string_view comment =
            stripped.size() < original_line.size() ? original_line.substr(stripped.size())
                                                   : std::string_view();
        const std::string indent(GetRenderedLuaIndent(original_line));

        std::string rebuilt = indent;
        if (is_local) {
            rebuilt += "local ";
        }
        rebuilt += std::string(lhs);
        rebuilt += " = {";
        for (std::size_t i = 0; i < entries.size(); ++i) {
            if (i != 0) {
                rebuilt += ", ";
            }
            rebuilt += entries[i];
        }
        rebuilt += "}";
        rebuilt += std::string(comment);
        return rebuilt;
    };

    const auto collect_safe_prefix_expressions_with_prefix =
        [](std::string_view line,
           std::string_view prefix,
           std::vector<std::string>* matches) {
            if (matches == nullptr) {
                return;
            }

            matches->clear();
            if (prefix.empty()) {
                return;
            }

            const std::string_view stripped = StripLuaLineComment(line);
            std::size_t search_from = 0;
            while (search_from < stripped.size()) {
                const std::size_t found = stripped.find(prefix, search_from);
                if (found == std::string_view::npos) {
                    break;
                }

                const bool boundary_before =
                    found == 0 ||
                    (!std::isalnum(static_cast<unsigned char>(stripped[found - 1])) &&
                     stripped[found - 1] != '_');
                if (!boundary_before) {
                    search_from = found + prefix.size();
                    continue;
                }

                std::size_t end = found + prefix.size();
                while (end < stripped.size()) {
                    const char ch = stripped[end];
                    if (std::isalnum(static_cast<unsigned char>(ch)) || ch == '_' || ch == '.' ||
                        ch == '[' || ch == ']') {
                        ++end;
                        continue;
                    }
                    break;
                }

                const std::string candidate =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(stripped.substr(found, end - found)),
                                                  nullptr);
                if (!candidate.empty() && IsRenderedSafePrefixExpression(candidate)) {
                    matches->push_back(candidate);
                }
                search_from = end;
            }
        };

    std::function<std::optional<ResolvedRenderedTempExpression>(std::size_t, std::string_view)>
        resolve_latest_expression =
            [&](std::size_t cursor,
                std::string_view temporary_name) -> std::optional<ResolvedRenderedTempExpression> {
        if (temporary_name.empty()) {
            return std::nullopt;
        }

        std::size_t probe = std::min(cursor + 1, lines->size());
        while (probe > 0) {
            --probe;

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression)) {
                continue;
            }

            const std::string normalized_lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            if (normalized_lhs != temporary_name) {
                continue;
            }

            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            const std::size_t self_use_count = CountIdentifierUses(normalized_rhs, temporary_name);
            if (self_use_count == 0) {
                return ResolvedRenderedTempExpression{normalized_rhs, probe};
            }
            if (self_use_count != 1 || probe == 0) {
                return std::nullopt;
            }

            const std::optional<ResolvedRenderedTempExpression> previous =
                resolve_latest_expression(probe - 1, temporary_name);
            if (!previous.has_value()) {
                return std::nullopt;
            }

            std::string rewritten_rhs;
            if (!ReplaceRenderedSafePrefixExpressionChainUses(normalized_rhs,
                                                              temporary_name,
                                                              previous->expression,
                                                              &rewritten_rhs) ||
                rewritten_rhs.empty()) {
                return std::nullopt;
            }

            return ResolvedRenderedTempExpression{
                StripRenderedWholeParentheses(TrimLuaWhitespace(rewritten_rhs), nullptr),
                previous->start_line,
            };
        }

        return std::nullopt;
    };

    const auto collect_recent_expressions =
        [&](std::string_view temporary_name,
            std::size_t before_line,
            std::size_t required_count,
            bool allow_prefix_supplement,
            std::vector<std::string>* expressions) {
        if (expressions == nullptr) {
            return false;
        }

        expressions->clear();
        std::size_t cursor = before_line;
        while (expressions->size() < required_count) {
            const std::optional<ResolvedRenderedTempExpression> resolved =
                resolve_latest_expression(cursor, temporary_name);
            if (!resolved.has_value() || resolved->expression.empty()) {
                break;
            }

            expressions->push_back(resolved->expression);
            if (resolved->start_line == 0) {
                break;
            }
            cursor = resolved->start_line - 1;
        }

        if (allow_prefix_supplement && !expressions->empty() &&
            expressions->size() < required_count) {
            const std::string& sample_expression = expressions->back();
            const std::size_t dot = sample_expression.rfind('.');
            if (dot != std::string::npos && dot > 0) {
                const std::string prefix = sample_expression.substr(0, dot + 1);
                std::size_t probe = cursor + 1;
                while (probe > 0 && expressions->size() < required_count) {
                    --probe;
                    std::vector<std::string> matches;
                    collect_safe_prefix_expressions_with_prefix((*lines)[probe], prefix, &matches);
                    for (auto match_it = matches.rbegin();
                         match_it != matches.rend() && expressions->size() < required_count;
                         ++match_it) {
                        expressions->push_back(*match_it);
                    }
                }
            }
        }

        if (expressions->size() != required_count) {
            expressions->clear();
            return false;
        }

        std::reverse(expressions->begin(), expressions->end());
        return true;
    };

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaTempKeyedTableLiteralAliases";
    RunTableTempPlaceholderCleanupPassLoop(32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression)) {
                continue;
            }

            const std::string normalized_lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (normalized_rhs.size() < 2 || normalized_rhs.front() != '{' ||
                normalized_rhs.back() != '}') {
                continue;
            }

            const std::string literal_body =
                normalized_rhs.substr(1, normalized_rhs.size() - 2);
            std::vector<std::string> entries;
            if (!SplitRenderedTopLevelCommaChain(literal_body, &entries) || entries.empty()) {
                continue;
            }

            struct ParsedLiteralEntry {
                std::string original;
                bool is_assignment = false;
                std::string lhs_expression;
                std::string rhs_expression;
                std::string key_temporary;
                std::string value_temporary;
            };

            std::vector<ParsedLiteralEntry> parsed_entries;
            parsed_entries.reserve(entries.size());
            std::unordered_map<std::string, std::size_t> key_counts;
            std::unordered_map<std::string, std::size_t> value_counts;
            bool saw_broken_temporary_keys = false;
            for (const std::string& entry : entries) {
                ParsedLiteralEntry parsed_entry;
                parsed_entry.original = entry;

                bool entry_is_local = false;
                std::string entry_lhs_expression;
                std::string entry_rhs_expression;
                if (ParseRenderedSingleAssignmentLine(entry,
                                                      &entry_is_local,
                                                      &entry_lhs_expression,
                                                      &entry_rhs_expression) &&
                    !entry_is_local) {
                    parsed_entry.is_assignment = true;
                    parsed_entry.lhs_expression = StripRenderedWholeParentheses(
                        TrimLuaWhitespace(entry_lhs_expression), nullptr);
                    parsed_entry.rhs_expression = StripRenderedWholeParentheses(
                        TrimLuaWhitespace(entry_rhs_expression), nullptr);

                    if (IsRenderTemporaryName(parsed_entry.lhs_expression)) {
                        parsed_entry.key_temporary = parsed_entry.lhs_expression;
                        ++key_counts[parsed_entry.key_temporary];
                        saw_broken_temporary_keys = true;
                    }

                    const std::string rhs_temporary =
                        extract_temporary_base(parsed_entry.rhs_expression);
                    if (!rhs_temporary.empty()) {
                        parsed_entry.value_temporary = rhs_temporary;
                        ++value_counts[rhs_temporary];
                    }
                }

                parsed_entries.push_back(std::move(parsed_entry));
            }

            if (!saw_broken_temporary_keys || line_index == 0) {
                continue;
            }

            std::unordered_map<std::string, std::vector<std::string>> key_history;
            std::unordered_map<std::string, std::size_t> key_offsets;
            bool failed = false;
            for (const auto& [temporary_name, count] : key_counts) {
                std::vector<std::string> expressions;
                if (!collect_recent_expressions(
                        temporary_name, line_index - 1, count, true, &expressions)) {
                    failed = true;
                    break;
                }
                key_history.emplace(temporary_name, std::move(expressions));
            }
            if (failed) {
                continue;
            }

            std::unordered_map<std::string, std::vector<std::string>> value_history;
            std::unordered_map<std::string, std::size_t> value_offsets;
            for (const auto& [temporary_name, count] : value_counts) {
                std::vector<std::string> expressions;
                if (!collect_recent_expressions(
                        temporary_name, line_index - 1, count, false, &expressions)) {
                    failed = true;
                    break;
                }
                value_history.emplace(temporary_name, std::move(expressions));
            }
            if (failed) {
                continue;
            }

            std::vector<std::string> rewritten_entries;
            rewritten_entries.reserve(parsed_entries.size());
            bool line_changed = false;
            for (const ParsedLiteralEntry& parsed_entry : parsed_entries) {
                if (!parsed_entry.is_assignment) {
                    rewritten_entries.push_back(parsed_entry.original);
                    continue;
                }

                std::string rewritten_lhs = parsed_entry.lhs_expression;
                if (!parsed_entry.key_temporary.empty()) {
                    const auto history_it = key_history.find(parsed_entry.key_temporary);
                    const auto offset_it = key_offsets.find(parsed_entry.key_temporary);
                    const std::size_t offset =
                        offset_it == key_offsets.end() ? 0 : offset_it->second;
                    if (history_it == key_history.end() || offset >= history_it->second.size()) {
                        failed = true;
                        break;
                    }
                    rewritten_lhs = "[" + history_it->second[offset] + "]";
                    key_offsets[parsed_entry.key_temporary] = offset + 1;
                    line_changed = true;
                } else if (!IsValidLuaIdentifier(rewritten_lhs) &&
                           !(rewritten_lhs.front() == '[' && rewritten_lhs.back() == ']') &&
                           IsRenderedSafePrefixExpression(rewritten_lhs)) {
                    rewritten_lhs = "[" + rewritten_lhs + "]";
                    line_changed = true;
                }

                std::string rewritten_rhs = parsed_entry.rhs_expression;
                if (!parsed_entry.value_temporary.empty()) {
                    const auto history_it = value_history.find(parsed_entry.value_temporary);
                    const auto offset_it = value_offsets.find(parsed_entry.value_temporary);
                    const std::size_t offset =
                        offset_it == value_offsets.end() ? 0 : offset_it->second;
                    if (history_it == value_history.end() || offset >= history_it->second.size()) {
                        failed = true;
                        break;
                    }

                    std::string replaced_rhs;
                    if (!ReplaceRenderedSafePrefixExpressionChainUses(
                            rewritten_rhs,
                            parsed_entry.value_temporary,
                            history_it->second[offset],
                            &replaced_rhs) ||
                        replaced_rhs.empty()) {
                        failed = true;
                        break;
                    }
                    rewritten_rhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(replaced_rhs), nullptr);
                    value_offsets[parsed_entry.value_temporary] = offset + 1;
                    line_changed = true;
                }

                rewritten_entries.push_back(rewritten_lhs + " = " + rewritten_rhs);
            }

            if (failed || !line_changed) {
                continue;
            }

            (*lines)[line_index] = rebuild_literal_line((*lines)[line_index],
                                                        is_local,
                                                        normalized_lhs,
                                                        rewritten_entries);
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaNestedTableLiteralPlaceholders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_placeholder = [](std::string_view line,
                                      int array_index_hint,
                                      std::string* key_rendered,
                                      std::string* nested_prefix,
                                      bool* emits_bare_array) -> bool {
        std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (trimmed.empty()) {
            return false;
        }
        if (trimmed.back() == ',') {
            trimmed.remove_suffix(1);
            trimmed = TrimLuaWhitespace(trimmed);
        }
        if (trimmed.empty()) {
            return false;
        }

        if (trimmed == "{}") {
            if (array_index_hint <= 0) {
                return false;
            }
            if (key_rendered != nullptr) {
                *key_rendered = "[" + std::to_string(array_index_hint) + "]";
            }
            if (nested_prefix != nullptr) {
                *nested_prefix = "[" + std::to_string(array_index_hint) + "]";
            }
            if (emits_bare_array != nullptr) {
                *emits_bare_array = true;
            }
            return true;
        }

        const std::size_t equal = trimmed.find('=');
        if (equal == std::string_view::npos) {
            return false;
        }

        const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
        const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
        if (lhs.empty() || rhs != "{}") {
            return false;
        }

        std::string rendered_key;
        std::string rendered_prefix;
        if (IsValidLuaIdentifier(lhs)) {
            rendered_key = std::string(lhs);
            rendered_prefix = "." + rendered_key;
        } else if (lhs.front() == '[' && lhs.back() == ']') {
            rendered_key = std::string(lhs);
            rendered_prefix = rendered_key;
        } else {
            return false;
        }

        if (key_rendered != nullptr) {
            *key_rendered = rendered_key;
        }
        if (nested_prefix != nullptr) {
            *nested_prefix = rendered_prefix;
        }
        if (emits_bare_array != nullptr) {
            *emits_bare_array = false;
        }
        return true;
    };

    const auto references_nested_prefix =
        [](std::string_view line, std::string_view prefix) -> bool {
            const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
            if (prefix.empty() || trimmed.size() < prefix.size() ||
                trimmed.substr(0, prefix.size()) != prefix) {
                return false;
            }
            if (trimmed.size() == prefix.size()) {
                return true;
            }

            const char next = trimmed[prefix.size()];
            return next == '.' || next == '[' || next == ':' || next == '(' ||
                   next == '=' || std::isspace(static_cast<unsigned char>(next)) != 0;
        };
    const auto is_nested_target_prefix =
        [&](std::string_view candidate, std::string_view prefix) -> bool {
            if (prefix.empty() || candidate.size() < prefix.size() ||
                candidate.substr(0, prefix.size()) != prefix) {
                return false;
            }
            if (candidate.size() == prefix.size()) {
                return true;
            }

            const char next = candidate[prefix.size()];
            return next == '.' || next == '[' || next == ':' || next == '(';
        };

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaNestedTableLiteralPlaceholders";
    RunTableTempPlaceholderCleanupPassLoop(32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                    &indent,
                                                    &lhs_prefix,
                                                    &table_name)) {
                continue;
            }

            const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, line_index);
            if (table_end >= lines->size() || table_end <= line_index) {
                continue;
            }

            struct PlaceholderInfo {
                std::size_t line_index = 0;
                std::string key_rendered;
                std::string full_prefix;
                bool emits_bare_array = false;
            };

            std::vector<PlaceholderInfo> placeholders;
            int top_level_brace_depth = 1;
            int expected_array_index = 1;
            for (std::size_t body_line = line_index + 1; body_line < table_end; ++body_line) {
                const int brace_depth_before_line = top_level_brace_depth;
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[body_line]));
                if (brace_depth_before_line == 1 && !trimmed.empty() && trimmed != "}" &&
                    trimmed != "}," && trimmed != "end" && trimmed != "end,") {
                    std::string key_rendered;
                    std::string nested_suffix;
                    bool emits_bare_array = false;
                    if (parse_placeholder((*lines)[body_line],
                                          expected_array_index,
                                          &key_rendered,
                                          &nested_suffix,
                                          &emits_bare_array)) {
                        PlaceholderInfo info;
                        info.line_index = body_line;
                        info.key_rendered = std::move(key_rendered);
                        info.full_prefix = table_name + nested_suffix;
                        info.emits_bare_array = emits_bare_array;
                        placeholders.push_back(std::move(info));
                    }

                    std::string_view trimmed_no_comma = trimmed;
                    if (!trimmed_no_comma.empty() && trimmed_no_comma.back() == ',') {
                        trimmed_no_comma.remove_suffix(1);
                        trimmed_no_comma = TrimLuaWhitespace(trimmed_no_comma);
                    }
                    if (trimmed_no_comma == "{}" ||
                        (!trimmed_no_comma.empty() &&
                         trimmed_no_comma.find('=') == std::string_view::npos &&
                         trimmed_no_comma != "{" && trimmed_no_comma != "}")) {
                        ++expected_array_index;
                    }
                }
                top_level_brace_depth += ComputeRenderedBraceDeltaIgnoringStringsAndComments(
                    (*lines)[body_line]);
            }

            if (placeholders.empty()) {
                continue;
            }

            std::vector<std::pair<std::size_t, std::vector<std::string>>> replacements;
            std::vector<std::size_t> lines_to_erase;
            bool failed = false;
            for (const PlaceholderInfo& placeholder : placeholders) {
                std::vector<std::string> nested_lines;
                std::vector<std::size_t> consumed_line_indexes;
                std::unordered_set<std::string> referenced_temporary_names;
                int expected_nested_array_index = 1;
                std::size_t probe_limit = lines->size();
                for (std::size_t boundary_probe = table_end + 1; boundary_probe < lines->size();
                     ++boundary_probe) {
                    const std::string_view boundary_trimmed =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[boundary_probe]));
                    if (boundary_trimmed.empty()) {
                        continue;
                    }
                    if (GetRenderedLuaIndent((*lines)[boundary_probe]) != indent) {
                        continue;
                    }

                    bool boundary_is_local = false;
                    std::string boundary_lhs;
                    std::string boundary_rhs;
                    if (!ParseRenderedSingleAssignmentLine((*lines)[boundary_probe],
                                                          &boundary_is_local,
                                                          &boundary_lhs,
                                                          &boundary_rhs) ||
                        boundary_is_local) {
                        continue;
                    }

                    const std::string_view normalized_boundary_lhs =
                        TrimLuaWhitespace(boundary_lhs);
                    if (is_nested_target_prefix(normalized_boundary_lhs, table_name)) {
                        continue;
                    }

                    probe_limit = boundary_probe;
                    break;
                }

                for (std::size_t probe = table_end + 1; probe < probe_limit; ++probe) {
                    if (IsAssignmentToIdentifier((*lines)[probe], table_name)) {
                        break;
                    }

                    const std::string_view trimmed_probe =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                    if (trimmed_probe.empty()) {
                        continue;
                    }
                    if (!references_nested_prefix((*lines)[probe],
                                                  placeholder.full_prefix)) {
                        continue;
                    }

                    std::string field_key;
                    std::string rhs_expression;
                    bool rhs_is_function = false;
                    int array_index = -1;
                    if (!ParseRenderedTableFieldAssignment((*lines)[probe],
                                                          placeholder.full_prefix,
                                                          &field_key,
                                                          &rhs_expression,
                                                          &rhs_is_function,
                                                          &array_index)) {
                        failed = true;
                        break;
                    }

                    if (rhs_is_function || rhs_expression == "{") {
                        failed = true;
                        break;
                    }
                    int rhs_paren_balance = 0;
                    int rhs_brace_balance = 0;
                    if (HasPositiveLuaExpressionBalance(rhs_expression,
                                                       &rhs_paren_balance,
                                                       &rhs_brace_balance)) {
                        failed = true;
                        break;
                    }
                    CollectRenderedTemporaryNames(rhs_expression, &referenced_temporary_names);

                    const bool is_array_entry =
                        array_index > 0 && array_index == expected_nested_array_index;
                    if (is_array_entry) {
                        nested_lines.push_back(indent + "    " + rhs_expression + ",");
                        ++expected_nested_array_index;
                    } else {
                        nested_lines.push_back(indent + "    " + field_key + " = " +
                                               rhs_expression + ",");
                    }
                    consumed_line_indexes.push_back(probe);
                }

                if (failed) {
                    break;
                }
                if (nested_lines.empty()) {
                    continue;
                }

                bool unsafe_forward_temporary = false;
                const std::size_t last_consumed_line = consumed_line_indexes.empty()
                                                           ? placeholder.line_index
                                                           : consumed_line_indexes.back();
                for (const std::string& temporary_name : referenced_temporary_names) {
                    if (HasRenderedTemporaryAssignmentInRange(*lines,
                                                              placeholder.line_index + 1,
                                                              last_consumed_line + 1,
                                                              temporary_name)) {
                        unsafe_forward_temporary = true;
                        break;
                    }
                }
                if (unsafe_forward_temporary) {
                    continue;
                }

                std::vector<std::string> replacement;
                if (placeholder.emits_bare_array) {
                    replacement.push_back(indent + "  {");
                } else {
                    replacement.push_back(indent + "  " + placeholder.key_rendered + " = {");
                }
                replacement.insert(replacement.end(), nested_lines.begin(), nested_lines.end());
                replacement.push_back(indent + "  },");
                replacements.emplace_back(placeholder.line_index, std::move(replacement));
                lines_to_erase.insert(lines_to_erase.end(),
                                      consumed_line_indexes.begin(),
                                      consumed_line_indexes.end());
            }

            if (failed || replacements.empty()) {
                continue;
            }

            std::sort(lines_to_erase.begin(), lines_to_erase.end());
            lines_to_erase.erase(std::unique(lines_to_erase.begin(), lines_to_erase.end()),
                                 lines_to_erase.end());

            for (auto erase_it = lines_to_erase.rbegin(); erase_it != lines_to_erase.rend(); ++erase_it) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(*erase_it));
            }

            std::sort(replacements.begin(),
                      replacements.end(),
                      [](const auto& lhs, const auto& rhs) { return lhs.first > rhs.first; });
            for (const auto& replacement_entry : replacements) {
                const std::size_t replace_line = replacement_entry.first;
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(replace_line));
                lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(replace_line),
                              replacement_entry.second.begin(),
                              replacement_entry.second.end());
            }

            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

}  // namespace refii::lua
