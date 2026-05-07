#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua {
namespace {

std::string NormalizeRenderedCallCleanupExpression(std::string_view value) {
    return StripRenderedWholeParentheses(TrimLuaWhitespace(value), nullptr);
}

std::size_t FindNextNonemptyRenderedLineIndex(const std::vector<std::string>& lines,
                                              std::size_t start) {
    std::size_t index = start;
    while (index < lines.size() &&
           TrimLuaWhitespace(StripLuaLineComment(lines[index])).empty()) {
        ++index;
    }
    return index;
}

bool ParseRenderedCallCleanupExpression(std::string_view expression,
                                        std::string* callee,
                                        std::vector<std::string>* arguments,
                                        bool normalize_expression) {
    if (callee == nullptr || arguments == nullptr) {
        return false;
    }

    std::string normalized_storage;
    if (normalize_expression) {
        normalized_storage = NormalizeRenderedCallCleanupExpression(expression);
        expression = normalized_storage;
    } else {
        expression = TrimLuaWhitespace(expression);
    }

    std::string_view call_callee;
    std::string_view call_arguments;
    if (expression.empty() ||
        !TryParseRenderedTrailingCall(expression, &call_callee, &call_arguments)) {
        return false;
    }

    *callee = std::string(TrimLuaWhitespace(call_callee));
    return SplitRenderedTopLevelCommaChain(call_arguments, arguments);
}

}  // namespace

bool SimplifyRenderedLuaLocalCallBootstrapGuards(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto normalize_arguments = [&](const std::vector<std::string>& arguments) {
        std::vector<std::string> normalized;
        normalized.reserve(arguments.size());
        for (const std::string& argument : arguments) {
            normalized.push_back(NormalizeRenderedCallCleanupExpression(argument));
        }
        return normalized;
    };
    const auto replace_exact_once = [](std::string_view haystack,
                                       std::string_view needle,
                                       std::string_view replacement,
                                       std::string* rewritten) -> bool {
        if (rewritten == nullptr || needle.empty()) {
            return false;
        }

        const std::size_t first = haystack.find(needle);
        if (first == std::string_view::npos || haystack.find(needle, first + needle.size()) !=
                                                   std::string_view::npos) {
            return false;
        }

        rewritten->assign(haystack.substr(0, first));
        rewritten->append(replacement);
        rewritten->append(haystack.substr(first + needle.size()));
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }
        (void)seed_is_local;

        std::string seed_callee;
        std::vector<std::string> seed_arguments;
        if (!ParseRenderedCallCleanupExpression(
                seed_rhs, &seed_callee, &seed_arguments, true) ||
            !IsValidLuaIdentifier(seed_callee)) {
            continue;
        }
        const std::vector<std::string> normalized_seed_arguments =
            normalize_arguments(seed_arguments);
        const std::string normalized_seed_expression =
            NormalizeRenderedCallCleanupExpression(seed_rhs);

        std::size_t shadow_index =
            FindNextNonemptyRenderedLineIndex(*lines, line_index + 1);
        std::size_t intermediary_index = lines->size();
        if (shadow_index < lines->size()) {
            bool intermediary_is_local = false;
            std::string intermediary_lhs;
            std::string intermediary_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[shadow_index],
                                                  &intermediary_is_local,
                                                  &intermediary_lhs,
                                                  &intermediary_rhs) &&
                intermediary_lhs != seed_lhs) {
                const std::string normalized_intermediary_rhs =
                    NormalizeRenderedCallCleanupExpression(intermediary_rhs);
                const std::string rawget_seed_lhs = "rawget(" + seed_lhs + ", \"_M\")";
                const std::string rawget_seed_expression =
                    "rawget(" + normalized_seed_expression + ", \"_M\")";
                if (normalized_intermediary_rhs == rawget_seed_lhs ||
                    normalized_intermediary_rhs == rawget_seed_expression) {
                    intermediary_index = shadow_index;
                    shadow_index = FindNextNonemptyRenderedLineIndex(*lines, shadow_index + 1);
                }
            }
        }
        bool remove_shadow = false;
        if (shadow_index < lines->size()) {
            bool shadow_is_local = false;
            std::string shadow_lhs;
            std::string shadow_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[shadow_index],
                                                  &shadow_is_local,
                                                  &shadow_lhs,
                                                  &shadow_rhs) &&
                shadow_is_local && shadow_lhs == seed_lhs) {
                std::string shadow_callee;
                std::vector<std::string> shadow_arguments;
                if (ParseRenderedCallCleanupExpression(
                        shadow_rhs, &shadow_callee, &shadow_arguments, true) &&
                    shadow_callee == seed_lhs &&
                    normalize_arguments(shadow_arguments) == normalized_seed_arguments) {
                    remove_shadow = true;
                }
            }
        }

        const std::size_t guard_index =
            FindNextNonemptyRenderedLineIndex(*lines,
                                              remove_shadow ? shadow_index + 1
                                                            : (intermediary_index < lines->size()
                                                                   ? intermediary_index + 1
                                                                   : line_index + 1));
        if (guard_index >= lines->size()) {
            continue;
        }

        bool local_changed = false;
        std::string if_condition;
        if (TryExtractRenderedIfCondition((*lines)[guard_index], &if_condition)) {
            const std::string normalized_condition =
                NormalizeRenderedCallCleanupExpression(if_condition);
            const std::string rawget_seed_expression =
                "rawget(" + normalized_seed_expression + ", \"_M\")";
            const std::string rawget_seed_callee = "rawget(" + seed_callee + ", \"_M\")";
            if (normalized_condition == rawget_seed_expression ||
                normalized_condition == rawget_seed_callee) {
                const std::string indent(GetRenderedLuaIndent((*lines)[guard_index]));
                (*lines)[guard_index] = indent + "if rawget(" + seed_lhs + ", \"_M\") then";
                local_changed = true;
            } else {
                const std::string seed_index_access = normalized_seed_expression + "[";
                std::string rewritten_condition;
                if (replace_exact_once(normalized_condition,
                                       seed_index_access,
                                       seed_lhs + "[",
                                       &rewritten_condition)) {
                    const std::string indent(GetRenderedLuaIndent((*lines)[guard_index]));
                    (*lines)[guard_index] = indent + "if " + rewritten_condition + " then";
                    local_changed = true;
                }
            }
        } else {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[guard_index]));
            if (trimmed.starts_with("assert(")) {
                const std::string seed_index_access = normalized_seed_expression + "[";
                const std::string seed_callee_index_access = seed_callee + "[";
                std::string rewritten_assert;
                if (replace_exact_once(trimmed,
                                       seed_index_access,
                                       seed_lhs + "[",
                                       &rewritten_assert) ||
                    replace_exact_once(trimmed,
                                       seed_callee_index_access,
                                       seed_lhs + "[",
                                       &rewritten_assert)) {
                    const std::string indent(GetRenderedLuaIndent((*lines)[guard_index]));
                    (*lines)[guard_index] = indent + rewritten_assert;
                    local_changed = true;
                }
            }
        }

        if (!local_changed) {
            continue;
        }

        if (remove_shadow) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(shadow_index));
        }
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool NormalizeRenderedLuaPerformActionExLoopArgumentForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto has_loop_type_before_call =
        [&](std::size_t first_line, std::size_t call_line, std::string_view variable) {
            const std::string prefix = std::string(variable) + ".Type";
            for (std::size_t probe = first_line + 1; probe < call_line; ++probe) {
                const std::string_view trimmed = trim_line((*lines)[probe]);
                if (trimmed.starts_with(prefix) &&
                    trimmed.find("EScriptableAction.LOOP") != std::string_view::npos) {
                    return true;
                }
            }
            return false;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        constexpr std::string_view kNeedle = ":PerformActionEx(loop)";
        if (trim_line((*lines)[line_index]).find(kNeedle) == std::string_view::npos) {
            continue;
        }

        std::string replacement_argument;
        const std::size_t scan_begin = line_index > 32 ? line_index - 32 : 0;
        bool saw_explicit_loop_local = false;
        for (std::size_t probe = line_index; probe-- > scan_begin;) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &is_local,
                                                   &lhs,
                                                   &rhs)) {
                continue;
            }
            (void)is_local;

            const std::string lhs_name = std::string(TrimLuaWhitespace(lhs));
            if (lhs_name == "loop") {
                saw_explicit_loop_local = true;
                break;
            }
            if (!IsValidLuaIdentifier(lhs_name) || IsLuaKeyword(lhs_name)) {
                continue;
            }
            const bool rhs_seeds_loop =
                rhs.find("EScriptableAction.LOOP") != std::string::npos;
            const bool rhs_seeds_table = rhs.find('{') != std::string::npos;
            if (!rhs_seeds_loop &&
                !(rhs_seeds_table &&
                  has_loop_type_before_call(probe, line_index, lhs_name))) {
                continue;
            }
            replacement_argument = lhs_name;
            break;
        }
        if (saw_explicit_loop_local || replacement_argument.empty()) {
            continue;
        }

        const std::size_t use = (*lines)[line_index].find(kNeedle);
        if (use == std::string::npos) {
            continue;
        }
        (*lines)[line_index].replace(use,
                                     kNeedle.size(),
                                     ":PerformActionEx(" + replacement_argument + ")");
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaLoopActionTableAliasForParity(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto looks_like_synthetic_register = [](std::string_view name) {
        if (name.size() < 2 || name.front() != 'r') {
            return false;
        }
        for (std::size_t index = 1; index < name.size(); ++index) {
            if (!std::isdigit(static_cast<unsigned char>(name[index]))) {
                return false;
            }
        }
        return true;
    };
    const auto count_identifier_uses_in_range =
        [&](std::string_view name, std::size_t first_line, std::size_t last_line) {
            std::size_t count = 0;
            for (std::size_t probe = first_line;
                 probe < lines->size() && probe <= last_line;
                 ++probe) {
                count += CountIdentifierUses((*lines)[probe], name);
            }
            return count;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            seed_is_local ||
            !looks_like_synthetic_register(seed_lhs) ||
            seed_rhs.find("EScriptableAction.BATCH") == std::string::npos ||
            seed_rhs.find("Actions") == std::string::npos) {
            continue;
        }

        std::string alias_name;
        std::size_t alias_last_use = line_index;
        const std::size_t scan_end =
            std::min(lines->size(), line_index + static_cast<std::size_t>(32));
        for (std::size_t probe = line_index + 1; probe < scan_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            constexpr std::string_view kInsertPrefix = "table.insert(";
            const std::size_t insert_pos = trimmed.find(kInsertPrefix);
            if (insert_pos != std::string_view::npos) {
                const std::size_t actions_pos =
                    trimmed.find(".Actions", insert_pos + kInsertPrefix.size());
                if (actions_pos != std::string_view::npos) {
                    const std::string_view candidate = TrimLuaWhitespace(trimmed.substr(
                        insert_pos + kInsertPrefix.size(),
                        actions_pos - (insert_pos + kInsertPrefix.size())));
                    if (IsValidLuaIdentifier(candidate) && !IsLuaKeyword(candidate)) {
                        alias_name = std::string(candidate);
                        alias_last_use = probe;
                        break;
                    }
                }
            }
        }
        if (alias_name.empty() || alias_name == seed_lhs ||
            looks_like_synthetic_register(alias_name)) {
            continue;
        }

        bool has_loop_action_use = false;
        const std::string loop_action_assignment = ".LoopAction = " + alias_name;
        for (std::size_t probe = alias_last_use + 1; probe < scan_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.find(loop_action_assignment) != std::string_view::npos) {
                has_loop_action_use = true;
                alias_last_use = probe;
                break;
            }
        }
        if (!has_loop_action_use ||
            count_identifier_uses_in_range(seed_lhs, line_index + 1, alias_last_use) != 0) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "local " + alias_name + " = " + seed_rhs;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaRepeatedLocalCallPropertyUsesForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto replace_exact_once = [](std::string_view haystack,
                                       std::string_view needle,
                                       std::string_view replacement,
                                       std::string* rewritten) -> bool {
        if (rewritten == nullptr || needle.empty()) {
            return false;
        }
        const std::size_t first = haystack.find(needle);
        if (first == std::string_view::npos ||
            haystack.find(needle, first + needle.size()) != std::string_view::npos) {
            return false;
        }

        rewritten->assign(haystack.substr(0, first));
        rewritten->append(replacement);
        rewritten->append(haystack.substr(first + needle.size()));
        return true;
    };

    const auto assignment_targets_name =
        [](std::string_view line, std::string_view name) -> bool {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!ParseRenderedAssignmentLine(line, &is_local, &lhs_names, &rhs)) {
            return false;
        }
        (void)is_local;
        for (const std::string& lhs : lhs_names) {
            if (TrimLuaWhitespace(lhs) == name) {
                return true;
            }
        }
        return false;
    };
    const auto is_identifier_char = [](char value) -> bool {
        return std::isalnum(static_cast<unsigned char>(value)) || value == '_';
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }

        std::string seed_callee;
        std::vector<std::string> seed_arguments;
        if (!ParseRenderedCallCleanupExpression(
                seed_rhs, &seed_callee, &seed_arguments, true)) {
            continue;
        }

        const std::string normalized_seed =
            NormalizeRenderedCallCleanupExpression(seed_rhs);
        if (normalized_seed.find(':') != std::string::npos ||
            normalized_seed.find('.') == std::string::npos) {
            continue;
        }

        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        if (function_begin >= lines->size()) {
            continue;
        }
        const std::size_t function_end =
            FindRenderedFunctionBlockEnd(*lines, function_begin);
        if (function_end >= lines->size() || function_end <= line_index + 1) {
            continue;
        }

        const std::string repeated_prefix = normalized_seed + ".";
        for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (assignment_targets_name(trimmed, seed_lhs)) {
                break;
            }

            const std::size_t repeated_at = trimmed.find(repeated_prefix);
            if (repeated_at == std::string_view::npos) {
                continue;
            }
            if (repeated_at > 0 && is_identifier_char(trimmed[repeated_at - 1])) {
                continue;
            }

            std::size_t field_start = repeated_at + repeated_prefix.size();
            std::size_t field_end = field_start;
            while (field_end < trimmed.size() && is_identifier_char(trimmed[field_end])) {
                ++field_end;
            }
            if (field_end <= field_start) {
                continue;
            }

            const std::string replacement =
                seed_lhs + "." + std::string(trimmed.substr(field_start,
                                                             field_end - field_start));
            std::string rewritten;
            if (!replace_exact_once((*lines)[probe],
                                    std::string(trimmed.substr(repeated_at,
                                                               field_end - repeated_at)),
                                    replacement,
                                    &rewritten)) {
                continue;
            }

            (*lines)[probe] = std::move(rewritten);
            changed = true;
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaRepeatedLocalCallExpressionUsesForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_identifier_char = [](char value) -> bool {
        return std::isalnum(static_cast<unsigned char>(value)) || value == '_';
    };
    const auto replace_exact_once = [&](std::string_view haystack,
                                        std::string_view needle,
                                        std::string_view replacement,
                                        std::string* rewritten) -> bool {
        if (rewritten == nullptr || needle.empty()) {
            return false;
        }
        const std::size_t first = haystack.find(needle);
        if (first == std::string_view::npos ||
            haystack.find(needle, first + needle.size()) != std::string_view::npos) {
            return false;
        }
        if (first > 0 && is_identifier_char(haystack[first - 1])) {
            return false;
        }
        const std::size_t after = first + needle.size();
        if (after < haystack.size() && is_identifier_char(haystack[after])) {
            return false;
        }

        rewritten->assign(haystack.substr(0, first));
        rewritten->append(replacement);
        rewritten->append(haystack.substr(after));
        return true;
    };
    const auto assignment_targets_name =
        [](std::string_view line, std::string_view name) -> bool {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!ParseRenderedAssignmentLine(line, &is_local, &lhs_names, &rhs)) {
            return false;
        }
        (void)is_local;
        for (const std::string& lhs : lhs_names) {
            if (TrimLuaWhitespace(lhs) == name) {
                return true;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !seed_is_local || !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }

        std::string seed_callee;
        std::vector<std::string> seed_arguments;
        if (!ParseRenderedCallCleanupExpression(
                seed_rhs, &seed_callee, &seed_arguments, true)) {
            continue;
        }

        const std::string normalized_seed =
            NormalizeRenderedCallCleanupExpression(seed_rhs);
        if (normalized_seed.empty() || normalized_seed == seed_lhs ||
            normalized_seed.find('(') == std::string::npos ||
            normalized_seed.find(':') != std::string::npos) {
            continue;
        }

        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        if (function_begin >= lines->size()) {
            continue;
        }
        const std::size_t function_end =
            FindRenderedFunctionBlockEnd(*lines, function_begin);
        if (function_end >= lines->size() || function_end <= line_index + 1) {
            continue;
        }

        for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (assignment_targets_name(trimmed, seed_lhs)) {
                break;
            }
            if (trimmed.find(normalized_seed) == std::string_view::npos) {
                continue;
            }

            std::string rewritten;
            if (!replace_exact_once((*lines)[probe], normalized_seed, seed_lhs, &rewritten)) {
                continue;
            }

            (*lines)[probe] = std::move(rewritten);
            changed = true;
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaRepeatedLocalExpressionUsesForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_simple_literal_or_identifier = [](std::string_view expression) -> bool {
        expression = TrimLuaWhitespace(expression);
        if (IsValidLuaIdentifier(expression) || expression == "nil" ||
            expression == "true" || expression == "false") {
            return true;
        }
        return !expression.empty() &&
               (std::isdigit(static_cast<unsigned char>(expression.front())) ||
                expression.front() == '"' || expression.front() == '\'');
    };
    const auto assignment_targets_name =
        [](std::string_view line, std::string_view name) -> bool {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!ParseRenderedAssignmentLine(line, &is_local, &lhs_names, &rhs)) {
            return false;
        }
        (void)is_local;
        for (const std::string& lhs : lhs_names) {
            if (TrimLuaWhitespace(lhs) == name) {
                return true;
            }
        }
        return false;
    };
    const auto replace_exact_once = [](std::string_view haystack,
                                       std::string_view needle,
                                       std::string_view replacement,
                                       std::string* rewritten) -> bool {
        if (rewritten == nullptr || needle.empty()) {
            return false;
        }
        const std::size_t first = haystack.find(needle);
        if (first == std::string_view::npos ||
            haystack.find(needle, first + needle.size()) != std::string_view::npos) {
            return false;
        }
        rewritten->assign(haystack.substr(0, first));
        rewritten->append(replacement);
        rewritten->append(haystack.substr(first + needle.size()));
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !seed_is_local || !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }

        const std::string normalized_seed =
            NormalizeRenderedCallCleanupExpression(seed_rhs);
        std::vector<std::string> repeated_forms;
        repeated_forms.push_back(normalized_seed);
        for (std::string_view method_suffix :
             {":GetLength()", ":GetLengthXY()", ":GetSquaredLength()", ":GetSquaredLengthXY()"}) {
            if (std::string_view(normalized_seed).ends_with(method_suffix)) {
                const std::string base =
                    normalized_seed.substr(0, normalized_seed.size() - method_suffix.size());
                const std::string method_name =
                    std::string(method_suffix.substr(1, method_suffix.size() - 3));
                if (!base.empty()) {
                    repeated_forms.push_back(base + "." + method_name + "(" + base + ")");
                }
                break;
            }
        }
        const bool has_call = normalized_seed.find('(') != std::string::npos;
        const bool is_call_bearing_binary_member_expression =
            has_call &&
            (normalized_seed.find(" - ") != std::string::npos ||
             normalized_seed.find(" + ") != std::string::npos ||
             normalized_seed.find(" * ") != std::string::npos ||
             normalized_seed.find(" / ") != std::string::npos) &&
            normalized_seed.find('.') != std::string::npos;
        if (normalized_seed.size() < 10 ||
            (has_call && !is_call_bearing_binary_member_expression) ||
            is_simple_literal_or_identifier(normalized_seed) ||
            (normalized_seed.find('.') == std::string::npos &&
             normalized_seed.find('[') == std::string::npos)) {
            continue;
        }

        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        if (function_begin >= lines->size()) {
            continue;
        }
        const std::size_t function_end =
            FindRenderedFunctionBlockEnd(*lines, function_begin);
        if (function_end >= lines->size() || function_end <= line_index + 1) {
            continue;
        }

        for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (assignment_targets_name(trimmed, seed_lhs)) {
                break;
            }
            std::string rewritten;
            bool replaced = false;
            for (const std::string& repeated_form : repeated_forms) {
                if (trimmed.find(repeated_form) == std::string_view::npos) {
                    continue;
                }
                if (replace_exact_once((*lines)[probe], repeated_form, seed_lhs, &rewritten)) {
                    replaced = true;
                    break;
                }
            }
            if (!replaced) {
                continue;
            }

            (*lines)[probe] = std::move(rewritten);
            changed = true;
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaRepeatedMethodExpressionConditionsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_code = [](std::string_view line) {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto replace_exact_once = [](std::string_view haystack,
                                       std::string_view needle,
                                       std::string_view replacement,
                                       std::string* rewritten) -> bool {
        if (rewritten == nullptr || needle.empty()) {
            return false;
        }
        const std::size_t first = haystack.find(needle);
        if (first == std::string_view::npos ||
            haystack.find(needle, first + needle.size()) != std::string_view::npos) {
            return false;
        }
        rewritten->assign(haystack.substr(0, first));
        rewritten->append(replacement);
        rewritten->append(haystack.substr(first + needle.size()));
        return true;
    };
    const auto assignment_targets_name =
        [](std::string_view line, std::string_view name) -> bool {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!ParseRenderedAssignmentLine(line, &is_local, &lhs_names, &rhs)) {
            return false;
        }
        (void)is_local;
        for (const std::string& lhs : lhs_names) {
            if (TrimLuaWhitespace(lhs) == name) {
                return true;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }
        (void)seed_is_local;

        const std::string seed_expression =
            std::string(TrimLuaWhitespace(seed_rhs));
        std::string repeated_expression;
        for (std::string_view method_suffix :
             {":GetLength()", ":GetLengthXY()", ":GetSquaredLength()", ":GetSquaredLengthXY()"}) {
            if (!std::string_view(seed_expression).ends_with(method_suffix)) {
                continue;
            }
            const std::string base =
                seed_expression.substr(0, seed_expression.size() - method_suffix.size());
            const std::string method_name =
                std::string(method_suffix.substr(1, method_suffix.size() - 3));
            if (base.size() < 3 || base.front() != '(' || base.back() != ')' ||
                base.find(':') == std::string::npos) {
                continue;
            }
            repeated_expression = base + "." + method_name + "(" + base + ")";
            break;
        }
        if (repeated_expression.empty()) {
            continue;
        }

        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        if (function_begin >= lines->size()) {
            continue;
        }
        const std::size_t function_end =
            FindRenderedFunctionBlockEnd(*lines, function_begin);
        if (function_end >= lines->size() || function_end <= line_index + 1) {
            continue;
        }

        const std::size_t probe_end = std::min(function_end, line_index + 5);
        for (std::size_t probe = line_index + 1; probe < probe_end; ++probe) {
            const std::string_view trimmed = trim_code((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (assignment_targets_name(trimmed, seed_lhs)) {
                break;
            }
            std::string condition;
            if (!TryExtractRenderedIfCondition((*lines)[probe], &condition) ||
                condition.find(repeated_expression) == std::string::npos) {
                continue;
            }

            std::string rewritten;
            if (!replace_exact_once((*lines)[probe],
                                    repeated_expression,
                                    seed_lhs,
                                    &rewritten)) {
                continue;
            }
            (*lines)[probe] = std::move(rewritten);
            changed = true;
            break;
        }
    }

    return changed;
}

bool ExpandRenderedLuaPostCallLocalAliasesForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto assignment_targets_name =
        [](std::string_view line, std::string_view name) -> bool {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!ParseRenderedAssignmentLine(line, &is_local, &lhs_names, &rhs)) {
            return false;
        }
        (void)is_local;
        for (const std::string& lhs : lhs_names) {
            if (TrimLuaWhitespace(lhs) == name) {
                return true;
            }
        }
        return false;
    };
    const auto line_uses_any_argument = [](std::string_view line,
                                           const std::vector<std::string>& arguments) {
        const std::string_view stripped = StripLuaLineComment(line);
        for (const std::string& argument : arguments) {
            const std::string normalized =
                NormalizeRenderedCallCleanupExpression(argument);
            if (normalized.empty() || !IsValidLuaIdentifier(normalized)) {
                continue;
            }
            if (CountIdentifierUses(stripped, normalized) != 0) {
                return true;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !seed_is_local || !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }

        std::string seed_callee;
        std::vector<std::string> seed_arguments;
        if (!ParseRenderedCallCleanupExpression(
                seed_rhs, &seed_callee, &seed_arguments, true) ||
            seed_arguments.empty()) {
            continue;
        }
        const std::string normalized_seed =
            NormalizeRenderedCallCleanupExpression(seed_rhs);
        if (normalized_seed.empty() ||
            normalized_seed.find('(') == std::string::npos ||
            normalized_seed.find(':') != std::string::npos) {
            continue;
        }

        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        if (function_begin >= lines->size()) {
            continue;
        }
        const std::size_t function_end =
            FindRenderedFunctionBlockEnd(*lines, function_begin);
        if (function_end >= lines->size() || function_end <= line_index + 1) {
            continue;
        }

        bool saw_intervening_argument_call = false;
        const std::size_t probe_end = std::min(function_end, line_index + 10);
        for (std::size_t probe = line_index + 1; probe < probe_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (assignment_targets_name(trimmed, seed_lhs)) {
                break;
            }

            bool alias_is_local = false;
            std::string alias_lhs;
            std::string alias_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &alias_is_local,
                                                  &alias_lhs,
                                                  &alias_rhs) &&
                alias_is_local && IsValidLuaIdentifier(alias_lhs) &&
                NormalizeRenderedCallCleanupExpression(alias_rhs) == seed_lhs &&
                saw_intervening_argument_call) {
                (*lines)[probe] =
                    std::string(GetRenderedLuaIndent((*lines)[probe])) + "local " +
                    alias_lhs + " = " + normalized_seed;
                changed = true;
                break;
            }

            if (trimmed.find('(') != std::string_view::npos &&
                line_uses_any_argument(trimmed, seed_arguments)) {
                saw_intervening_argument_call = true;
            }
        }
    }

    return changed;
}

bool SimplifyRenderedLuaSelfCallShadowLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto join_arguments = [](const std::vector<std::string>& arguments) {
        std::ostringstream rebuilt;
        for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
            if (argument_index != 0) {
                rebuilt << ", ";
            }
            rebuilt << TrimLuaWhitespace(arguments[argument_index]);
        }
        return rebuilt.str();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }

        const std::size_t shadow_index =
            FindNextNonemptyRenderedLineIndex(*lines, line_index + 1);
        if (shadow_index >= lines->size()) {
            continue;
        }

        bool shadow_is_local = false;
        std::string shadow_lhs;
        std::string shadow_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[shadow_index],
                                               &shadow_is_local,
                                               &shadow_lhs,
                                               &shadow_rhs) ||
            shadow_lhs != seed_lhs) {
            continue;
        }

        std::string shadow_callee;
        std::vector<std::string> shadow_arguments;
        if (!ParseRenderedSingleCallLine(shadow_rhs, &shadow_callee, &shadow_arguments) ||
            NormalizeRenderedCallCleanupExpression(shadow_callee) != seed_lhs) {
            continue;
        }

        std::string seed_call_callee;
        std::vector<std::string> seed_call_arguments;
        const bool seed_is_call =
            ParseRenderedSingleCallLine(seed_rhs, &seed_call_callee, &seed_call_arguments);
        const std::string normalized_seed_rhs =
            NormalizeRenderedCallCleanupExpression(seed_rhs);

        if (!seed_is_call) {
            const std::string rebuilt_expression =
                normalized_seed_rhs + "(" + join_arguments(shadow_arguments) + ")";
            const std::string indent(GetRenderedLuaIndent((*lines)[shadow_index]));
            (*lines)[shadow_index] =
                indent + "local " + seed_lhs + " = " + rebuilt_expression;

            const std::size_t rewrite_end =
                std::min(lines->size(), static_cast<std::size_t>(shadow_index + 4));
            for (std::size_t probe = shadow_index + 1; probe < rewrite_end; ++probe) {
                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                     &probe_is_local,
                                                     &probe_lhs,
                                                     &probe_rhs) &&
                    probe_lhs == seed_lhs) {
                    break;
                }

                std::string rewritten;
                if (ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine((*lines)[probe],
                                                                                rebuilt_expression,
                                                                                seed_lhs,
                                                                                &rewritten) &&
                    !rewritten.empty()) {
                    (*lines)[probe] = std::move(rewritten);
                }
            }

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            if (line_index != 0) {
                --line_index;
            }
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(shadow_index));
        changed = true;

        const std::size_t guard_index =
            FindNextNonemptyRenderedLineIndex(*lines, line_index + 1);
        if (guard_index >= lines->size()) {
            if (line_index != 0) {
                --line_index;
            }
            continue;
        }

        std::string condition;
        if (TryExtractRenderedIfCondition((*lines)[guard_index], &condition)) {
            const std::string normalized_condition =
                NormalizeRenderedCallCleanupExpression(condition);
            const std::string normalized_seed_callee =
                NormalizeRenderedCallCleanupExpression(seed_call_callee);

            bool negate = false;
            std::string operand = normalized_condition;
            if (operand.starts_with("not ")) {
                negate = true;
                operand = NormalizeRenderedCallCleanupExpression(operand.substr(4));
            }

            if (operand == normalized_seed_rhs || operand == normalized_seed_callee) {
                const std::string indent(GetRenderedLuaIndent((*lines)[guard_index]));
                (*lines)[guard_index] =
                    indent + "if " + (negate ? "not " : std::string()) + seed_lhs + " then";
            }
        }

        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool LocalizeRenderedLuaGroupMindAttachedGuardCallsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto unquote_string_literal = [](std::string_view expression,
                                           std::string* value) -> bool {
        if (value == nullptr) {
            return false;
        }
        expression = TrimLuaWhitespace(expression);
        if (expression.size() < 2) {
            return false;
        }
        const char quote = expression.front();
        if ((quote != '"' && quote != '\'') || expression.back() != quote) {
            return false;
        }

        value->clear();
        value->reserve(expression.size() - 2);
        bool escaped = false;
        for (std::size_t i = 1; i + 1 < expression.size(); ++i) {
            const char ch = expression[i];
            if (escaped) {
                value->push_back(ch);
                escaped = false;
                continue;
            }
            if (ch == '\\') {
                escaped = true;
                continue;
            }
            value->push_back(ch);
        }
        return !value->empty();
    };

    const auto make_snake_identifier = [](std::string_view value) {
        std::string identifier;
        identifier.reserve(value.size() + 4);
        bool previous_was_word = false;
        bool previous_was_lower_or_digit = false;
        for (std::size_t i = 0; i < value.size(); ++i) {
            const unsigned char ch = static_cast<unsigned char>(value[i]);
            if (std::isalnum(ch) == 0) {
                if (previous_was_word && !identifier.empty() && identifier.back() != '_') {
                    identifier.push_back('_');
                }
                previous_was_word = false;
                previous_was_lower_or_digit = false;
                continue;
            }

            const bool is_upper = std::isupper(ch) != 0;
            if (is_upper && previous_was_lower_or_digit && !identifier.empty() &&
                identifier.back() != '_') {
                identifier.push_back('_');
            }
            identifier.push_back(
                static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
            previous_was_word = true;
            previous_was_lower_or_digit = std::islower(ch) != 0 || std::isdigit(ch) != 0;
        }

        while (!identifier.empty() && identifier.back() == '_') {
            identifier.pop_back();
        }
        if (identifier.empty() || std::isdigit(static_cast<unsigned char>(identifier.front())) !=
                                      0) {
            return std::string();
        }
        return IsValidLuaIdentifier(identifier) ? identifier : std::string();
    };

    const auto local_name_is_already_visible =
        [](const std::vector<std::string>& source_lines,
           std::size_t line_index,
           std::string_view name) {
            std::size_t begin = line_index;
            while (begin > 0) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment(source_lines[begin - 1]));
                if (IsRenderedFunctionStartLine(trimmed)) {
                    break;
                }
                --begin;
            }
            for (std::size_t probe = begin; probe < line_index; ++probe) {
                if (CountIdentifierKeyword(StripLuaLineComment(source_lines[probe]), name) != 0) {
                    return true;
                }
            }
            return false;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition)) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedCallCleanupExpression(condition, &callee, &arguments, true) ||
            NormalizeRenderedCallCleanupExpression(callee) !=
                "GroupMindManager:GetGroupMindAttachedToEntity" ||
            arguments.empty()) {
            continue;
        }

        std::string groupmind_name;
        if (!unquote_string_literal(arguments.front(), &groupmind_name)) {
            continue;
        }
        const std::string local_name = make_snake_identifier(groupmind_name);
        if (local_name.empty() ||
            local_name_is_already_visible(*lines, line_index, local_name)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string normalized_condition =
            NormalizeRenderedCallCleanupExpression(condition);
        (*lines)[line_index] = indent + "if " + local_name + " then";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      indent + "local " + local_name + " = " + normalized_condition);
        ++line_index;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaExplicitSelfMethodArguments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto rewrite_explicit_self_call_expression =
        [&](std::string_view expression, std::string* rewritten_out) -> bool {
            if (rewritten_out == nullptr) {
                return false;
            }

            const std::string_view trimmed_expression = TrimLuaWhitespace(expression);
            if (trimmed_expression.empty()) {
                return false;
            }

            bool negated = false;
            std::string_view operand = trimmed_expression;
            if (StartsWithLuaKeyword(operand, "not")) {
                negated = true;
                operand = TrimLuaWhitespace(operand.substr(3));
                if (operand.empty()) {
                    return false;
                }
            }

            std::string callee;
            std::vector<std::string> arguments;
            if (!ParseRenderedCallCleanupExpression(
                    operand, &callee, &arguments, false) ||
                arguments.empty()) {
                return false;
            }
            std::string rebuilt;
            if (!TryCanonicalizeRenderedExplicitSelfMethodCall(callee, arguments, &rebuilt)) {
                return false;
            }
            if (negated) {
                rebuilt = "not " + rebuilt;
            }
            *rewritten_out = std::move(rebuilt);
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string original_line = (*lines)[line_index];
        const std::string_view stripped = StripLuaLineComment(original_line);
        const std::string_view comment =
            stripped.size() < original_line.size()
                ? std::string_view(original_line).substr(stripped.size())
                : std::string_view();
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent(original_line));
        bool is_assignment = false;
        bool is_local = false;
        std::string lhs;
        std::string expression(trimmed);
        std::string rhs_expression;
        if (ParseRenderedSingleAssignmentLine(trimmed, &is_local, &lhs, &rhs_expression) &&
            !lhs.empty()) {
            expression = std::move(rhs_expression);
            is_assignment = true;
        }

        std::string guard_condition;
        std::string guard_prefix;
        std::string guard_suffix;
        bool is_guard_condition_line = false;
        if (!is_assignment) {
            if (TryExtractRenderedIfCondition(trimmed, &guard_condition)) {
                guard_prefix = "if ";
                guard_suffix = " then";
                is_guard_condition_line = true;
            } else if (trimmed.starts_with("while ") && trimmed.ends_with(" do")) {
                guard_condition =
                    std::string(TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9)));
                if (!guard_condition.empty()) {
                    guard_prefix = "while ";
                    guard_suffix = " do";
                    is_guard_condition_line = true;
                }
            } else if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                guard_condition =
                    std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
                if (!guard_condition.empty()) {
                    guard_prefix = "elseif ";
                    guard_suffix = " then";
                    is_guard_condition_line = true;
                }
            }
        }

        if (is_guard_condition_line) {
            std::string rewritten_condition;
            if (rewrite_explicit_self_call_expression(guard_condition, &rewritten_condition)) {
                std::string rebuilt = indent + guard_prefix + rewritten_condition + guard_suffix;
                rebuilt += std::string(comment);
                if (rebuilt != original_line) {
                    (*lines)[line_index] = std::move(rebuilt);
                    changed = true;
                }
            }
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedCallCleanupExpression(
                expression, &callee, &arguments, false) ||
            arguments.empty()) {
            continue;
        }
        std::string rebuilt;
        if (!TryCanonicalizeRenderedExplicitSelfMethodCall(callee, arguments, &rebuilt)) {
            continue;
        }
        if (is_assignment) {
            rebuilt = indent + (is_local ? "local " : std::string()) + lhs + " = " + rebuilt;
        } else {
            rebuilt = indent + rebuilt;
        }
        rebuilt += std::string(comment);

        if (rebuilt != original_line) {
            (*lines)[line_index] = std::move(rebuilt);
            changed = true;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedCallArgumentFallbackLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto extract_receiver_base = [](std::string_view expression) -> std::string {
        expression = TrimLuaWhitespace(expression);
        const std::size_t boundary = expression.find_first_of(".:[");
        if (boundary == std::string_view::npos) {
            return std::string(expression);
        }
        return std::string(expression.substr(0, boundary));
    };

    bool changed = false;
    std::size_t seed_counter = 0;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition)) {
            continue;
        }
        condition = std::string(TrimLuaWhitespace(condition));
        if (!StartsWithLuaKeyword(condition, "not")) {
            continue;
        }

        const std::string seed_expression = std::string(TrimLuaWhitespace(condition.substr(3)));
        if (seed_expression.empty() || seed_expression.find(" and ") != std::string::npos ||
            seed_expression.find(" or ") != std::string::npos) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine((*lines)[line_index + 1], &callee, &arguments) ||
            arguments.empty() ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        const std::string seed_receiver = extract_receiver_base(seed_expression);
        const std::string call_receiver = extract_receiver_base(callee);
        if (seed_receiver.empty() || seed_receiver != call_receiver) {
            continue;
        }

        const std::string first_argument = std::string(TrimLuaWhitespace(arguments.front()));
        if (first_argument.empty() || first_argument == seed_expression ||
            first_argument.find(seed_expression) != std::string::npos) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = indent + "  ";
        const std::string seeded_local =
            "refii_seeded_call_arg_" + std::to_string(++seed_counter);
        arguments.front() = seeded_local;

        std::ostringstream rebuilt_call;
        rebuilt_call << callee << "(";
        for (std::size_t arg_index = 0; arg_index < arguments.size(); ++arg_index) {
            if (arg_index != 0) {
                rebuilt_call << ", ";
            }
            rebuilt_call << arguments[arg_index];
        }
        rebuilt_call << ")";

        std::vector<std::string> replacement = {
            indent + "local " + seeded_local + " = " + seed_expression,
            indent + "if not " + seeded_local + " then",
            inner_indent + seeded_local + " = " + first_argument,
            indent + "end",
            indent + rebuilt_call.str(),
        };

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaCallArgumentBinaryMethodCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index], &is_local, &lhs, &rhs)) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine(rhs, &callee, &arguments) || arguments.empty()) {
            continue;
        }

        bool line_changed = false;
        for (std::string& argument : arguments) {
            std::string repaired_argument;
            if (TryRewriteRenderedBinaryMethodCarryExpressionRecursive(argument,
                                                                       &repaired_argument,
                                                                       0)) {
                argument = std::move(repaired_argument);
                line_changed = true;
            }
        }
        if (!line_changed) {
            continue;
        }

        std::ostringstream rebuilt_call;
        rebuilt_call << callee << "(";
        for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
            if (argument_index != 0) {
                rebuilt_call << ", ";
            }
            rebuilt_call << arguments[argument_index];
        }
        rebuilt_call << ")";

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + (is_local ? "local " : "") + lhs + " = " + rebuilt_call.str();
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaDeclaredMultiResultCallLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_single_call_line = [](std::string_view line,
                                           std::string* callee,
                                           std::vector<std::string>* arguments) {
        if (callee == nullptr || arguments == nullptr) {
            return false;
        }

        std::string_view callee_view;
        std::string_view arguments_view;
        if (!TryParseRenderedTrailingCall(line, &callee_view, &arguments_view)) {
            return false;
        }

        *callee = std::string(callee_view);
        return SplitRenderedTopLevelCommaChain(arguments_view, arguments);
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        std::vector<std::string> declared_names;
        if (!ParseRenderedLocalDeclarationLine((*lines)[line_index], &declared_names) ||
            declared_names.size() != 1 || !IsValidLuaIdentifier(declared_names.front())) {
            continue;
        }

        const std::string extra_name = declared_names.front();

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !assign_is_local || !IsValidLuaIdentifier(assign_lhs) ||
            assign_lhs == extra_name) {
            continue;
        }

        std::string call_callee;
        std::vector<std::string> call_arguments;
        if (!parse_single_call_line(assign_rhs, &call_callee, &call_arguments)) {
            continue;
        }

        bool used_after = false;
        const std::size_t scope_end = FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();
        for (std::size_t probe = line_index + 2; probe < probe_limit; ++probe) {
            if (IsRenderedFunctionStartLine((*lines)[probe])) {
                const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, probe);
                if (nested_end < lines->size() && nested_end > probe) {
                    probe = nested_end;
                    continue;
                }
            }
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], extra_name) ||
                IsAssignmentToIdentifier((*lines)[probe], extra_name)) {
                break;
            }
            if (CountIdentifierUses((*lines)[probe], extra_name) != 0) {
                used_after = true;
                break;
            }
        }
        if (!used_after) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] =
            indent + "local " + assign_lhs + ", " + extra_name + " = " + assign_rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateVarargLoopTables(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_block_end = [](const std::vector<std::string>& local_lines,
                                   std::size_t start_line) -> std::size_t {
        if (start_line >= local_lines.size()) {
            return local_lines.size();
        }

        int balance = 0;
        for (std::size_t probe = start_line; probe < local_lines.size(); ++probe) {
            balance += GetRenderedLuaBlockDelta(
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe])));
            if (probe > start_line && balance == 0) {
                return probe;
            }
        }
        return local_lines.size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string assign_rhs;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &assign_is_local,
                                         &lhs_names,
                                         &assign_rhs) ||
            !assign_is_local || lhs_names.size() != 1 ||
            !IsValidLuaIdentifier(lhs_names.front()) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr) != "{...}") {
            continue;
        }

        const std::string_view trimmed_header =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_header.starts_with("for ") || !trimmed_header.ends_with(" do")) {
            continue;
        }

        const std::string needle = "ipairs(" + lhs_names.front() + ")";
        const std::size_t found = (*lines)[line_index + 1].find(needle);
        if (found == std::string::npos) {
            continue;
        }

        const std::size_t loop_end = find_block_end(*lines, line_index + 1);
        if (loop_end >= lines->size()) {
            continue;
        }

        bool used_inside_loop = false;
        for (std::size_t probe = line_index + 2; probe < loop_end; ++probe) {
            if (CountIdentifierUses((*lines)[probe], lhs_names.front()) != 0) {
                used_inside_loop = true;
                break;
            }
        }
        if (used_inside_loop) {
            continue;
        }

        (*lines)[line_index + 1].replace(found, needle.size(), "ipairs({...})");
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

}  // namespace refii::lua
