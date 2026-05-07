#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <cctype>
#include <cstddef>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua {
namespace {

template <typename PassFn>
bool RunGamefaceCleanupPassLoop(int max_iterations, PassFn&& run_single_pass) {
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

bool SimplifyRenderedLuaGamefaceFunctionPlaceholders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    if (!HasRenderedLuaFunctionPlaceholderSignals(*lines)) {
        return false;
    }

    const auto extract_icon_texture_expression =
        [&](std::size_t line_index,
            const std::vector<std::vector<std::size_t>>& scope_paths,
            std::string* expression_out) -> bool {
            if (expression_out == nullptr || line_index >= scope_paths.size()) {
                return false;
            }

            const std::size_t scan_limit = std::min(lines->size(), line_index + 12);
            for (std::size_t probe = line_index + 1; probe < scan_limit; ++probe) {
                if (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index]) {
                    break;
                }

                const std::string_view stripped = StripLuaLineComment((*lines)[probe]);
                std::size_t cursor = stripped.find(".IconTexture");
                while (cursor != std::string_view::npos) {
                    std::size_t begin = cursor;
                    while (begin > 0) {
                        const char ch = stripped[begin - 1];
                        const unsigned char uch = static_cast<unsigned char>(ch);
                        if (std::isalnum(uch) != 0 || ch == '_' || ch == '.') {
                            --begin;
                            continue;
                        }
                        break;
                    }

                    const std::string candidate =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(stripped.substr(
                                                       begin,
                                                       cursor + std::string_view(".IconTexture").size() -
                                                           begin)),
                                                   nullptr);
                    if (candidate.ends_with(".IconTexture") &&
                        IsRenderedSafePrefixExpression(candidate)) {
                        *expression_out = candidate;
                        return true;
                    }

                    cursor = stripped.find(".IconTexture", cursor + 1);
                }
            }
            return false;
        };

    const auto extract_recent_placeholder_argument =
        [&](std::size_t line_index,
            const std::vector<std::vector<std::size_t>>& scope_paths,
            std::string* expression_out) -> bool {
            if (expression_out == nullptr || line_index >= scope_paths.size()) {
                return false;
            }

            for (std::size_t probe = line_index; probe > 0 && probe + 8 >= line_index;) {
                --probe;
                if (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index]) {
                    continue;
                }

                const std::string_view stripped = StripLuaLineComment((*lines)[probe]);
                for (std::string_view prefix : {"GetMenuItemScaleDelta(", "IsHeading("}) {
                    const std::size_t found = stripped.find(prefix);
                    if (found == std::string_view::npos) {
                        continue;
                    }

                    const std::size_t begin = found + prefix.size();
                    const std::size_t end = stripped.find(')', begin);
                    if (end == std::string_view::npos || end <= begin) {
                        continue;
                    }

                    const std::string candidate = StripRenderedWholeParentheses(
                        TrimLuaWhitespace(stripped.substr(begin, end - begin)),
                        nullptr);
                    if (IsRenderedSafePrefixExpression(candidate)) {
                        *expression_out = candidate;
                        return true;
                    }
                }
            }

            return false;
        };

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaGamefaceFunctionPlaceholders";
    RunGamefaceCleanupPassLoop(16, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (!trimmed.starts_with("local ")) {
                continue;
            }

            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            const std::string assign_lhs = std::string(
                TrimLuaWhitespace(trimmed.substr(6, equal - 6)));
            const std::string_view assign_rhs =
                TrimLuaWhitespace(trimmed.substr(equal + 1));
            if (!IsValidLuaIdentifier(assign_lhs) || assign_rhs != "function()") {
                continue;
            }

            std::size_t function_end = line_index + 1;
            while (function_end < lines->size() &&
                   TrimLuaWhitespace(StripLuaLineComment((*lines)[function_end])).empty()) {
                ++function_end;
            }
            if (function_end >= lines->size() ||
                TrimLuaWhitespace(StripLuaLineComment((*lines)[function_end])) != "end") {
                continue;
            }

            const std::size_t scan_limit = std::min(lines->size(), function_end + 8);
            bool local_rewritten = false;
            for (std::size_t probe = function_end + 1; probe < scan_limit; ++probe) {
                const std::string_view probe_trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (probe_trimmed.starts_with("function()(") ||
                    probe_trimmed.starts_with("function ()(")) {
                    const std::size_t placeholder_pos = (*lines)[probe].find("function()");
                    const std::size_t spaced_placeholder_pos =
                        (*lines)[probe].find("function ()");
                    if (placeholder_pos != std::string::npos) {
                        (*lines)[probe].replace(placeholder_pos, 10, assign_lhs);
                        changed = true;
                        local_rewritten = true;
                        continue;
                    }
                    if (spaced_placeholder_pos != std::string::npos) {
                        (*lines)[probe].replace(spaced_placeholder_pos, 11, assign_lhs);
                        changed = true;
                        local_rewritten = true;
                        continue;
                    }
                }

                std::string rewritten_line;
                if (!ReplaceRenderedFunctionPlaceholderUsesInLine((*lines)[probe],
                                                                 assign_lhs,
                                                                 &rewritten_line)) {
                    continue;
                }

                (*lines)[probe] = std::move(rewritten_line);
                changed = true;
                local_rewritten = true;
            }

            if (local_rewritten) {
                pass_changed = true;
                break;
            }
        }

        if (pass_changed) {
            return true;
        }

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool assign_is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!ParseRenderedAssignmentLineAllowFunctionPlaceholders((*lines)[line_index],
                                                                      &assign_is_local,
                                                                      &lhs_names,
                                                                      &rhs_expression) ||
                lhs_names.size() < 2) {
                continue;
            }

            std::string callee;
            std::vector<std::string> arguments;
            if (!ParseRenderedSingleCallLine(rhs_expression, &callee, &arguments)) {
                continue;
            }

            const std::string normalized_callee =
                StripRenderedWholeParentheses(TrimLuaWhitespace(callee), nullptr);
            if (!IsValidLuaIdentifier(normalized_callee)) {
                continue;
            }

            bool saw_placeholder = false;
            std::vector<std::string> rewritten_lhs = lhs_names;
            std::string callee_replacement;
            std::vector<std::string> function_placeholder_replacements;
            for (std::size_t lhs_index = 0; lhs_index < lhs_names.size(); ++lhs_index) {
                const std::string normalized_lhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_names[lhs_index]), nullptr);
                if (normalized_lhs == "function()" || normalized_lhs == "function ()") {
                    rewritten_lhs[lhs_index] = "__refii_" + normalized_callee + "_" +
                                               std::to_string(line_index + 1) + "_" +
                                               std::to_string(lhs_index + 1);
                    function_placeholder_replacements.push_back(rewritten_lhs[lhs_index]);
                    saw_placeholder = true;
                    continue;
                }

                if (normalized_lhs == normalized_callee) {
                    rewritten_lhs[lhs_index] = "__refii_" + normalized_callee + "_" +
                                               std::to_string(line_index + 1) + "_" +
                                               std::to_string(lhs_index + 1);
                    if (callee_replacement.empty()) {
                        callee_replacement = rewritten_lhs[lhs_index];
                    }
                    saw_placeholder = true;
                    continue;
                }

                if (!IsValidLuaIdentifier(normalized_lhs)) {
                    callee_replacement.clear();
                    saw_placeholder = false;
                    break;
                }

                rewritten_lhs[lhs_index] = normalized_lhs;
            }

            if (!saw_placeholder) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            std::ostringstream rebuilt_line;
            rebuilt_line << indent << "local ";
            for (std::size_t lhs_index = 0; lhs_index < rewritten_lhs.size(); ++lhs_index) {
                if (lhs_index != 0) {
                    rebuilt_line << ", ";
                }
                rebuilt_line << rewritten_lhs[lhs_index];
            }
            rebuilt_line << " = " << rhs_expression;
            (*lines)[line_index] = rebuilt_line.str();

            const std::size_t scan_limit = std::min(lines->size(), line_index + 20);
            for (std::size_t probe = line_index + 1; probe < scan_limit; ++probe) {
                if (probe >= scope_paths.size() || scope_paths[probe] != scope_paths[line_index]) {
                    break;
                }

                std::string rewritten_line = (*lines)[probe];
                std::string next_line;
                if (!callee_replacement.empty() &&
                    ReplaceRenderedIdentifierNonCallUses(rewritten_line,
                                                         normalized_callee,
                                                         callee_replacement,
                                                         &next_line)) {
                    rewritten_line = std::move(next_line);
                }

                if (!function_placeholder_replacements.empty() &&
                    ReplaceRenderedFunctionPlaceholderUsesInLineWithReplacements(
                        rewritten_line,
                        function_placeholder_replacements,
                        &next_line)) {
                    rewritten_line = std::move(next_line);
                }

                (*lines)[probe] = std::move(rewritten_line);
            }

            changed = true;
            pass_changed = true;
            break;
        }

        if (pass_changed) {
            return true;
        }

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            if (trimmed != "if function() then") {
                continue;
            }

            std::string condition_expression;
            if (!extract_icon_texture_expression(line_index, scope_paths, &condition_expression)) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            (*lines)[line_index] = indent + "if " + condition_expression + " then";
            changed = true;
            pass_changed = true;
            break;
        }

        if (pass_changed) {
            return true;
        }

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if ((*lines)[line_index].find("GetMenuItemTabOffset(function())") ==
                std::string::npos) {
                continue;
            }

            std::string argument_expression;
            if (!extract_recent_placeholder_argument(line_index,
                                                     scope_paths,
                                                     &argument_expression)) {
                continue;
            }

            std::string rewritten_line;
            if (!ReplaceRenderedFunctionPlaceholderUsesInLine((*lines)[line_index],
                                                              argument_expression,
                                                              &rewritten_line)) {
                continue;
            }

            (*lines)[line_index] = std::move(rewritten_line);
            changed = true;
            pass_changed = true;
            break;
        }

        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaResidualNamedFunctionCallPlaceholders(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    if (!HasRenderedLuaFunctionPlaceholderSignals(*lines)) {
        return false;
    }

    bool changed = false;
    constexpr const char* kStepName =
        "SimplifyRenderedLuaResidualNamedFunctionCallPlaceholders";
    RunGamefaceCleanupPassLoop(16, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            if (!(probe_trimmed.starts_with("function()(") ||
                  probe_trimmed.starts_with("function ()("))) {
                continue;
            }

            const std::size_t search_begin = line_index > 16 ? line_index - 16 : 0;
            for (std::size_t probe = line_index; probe-- > search_begin;) {
                const std::string_view stripped = StripLuaLineComment((*lines)[probe]);
                const std::string_view trimmed = TrimLuaWhitespace(stripped);
                if (!trimmed.starts_with("local ")) {
                    continue;
                }

                const std::size_t equal = trimmed.find('=');
                if (equal == std::string_view::npos) {
                    continue;
                }

                const std::string assign_lhs = std::string(
                    TrimLuaWhitespace(trimmed.substr(6, equal - 6)));
                const std::string_view assign_rhs =
                    TrimLuaWhitespace(trimmed.substr(equal + 1));
                if (!IsValidLuaIdentifier(assign_lhs) ||
                    (assign_rhs != "function()" && assign_rhs != "function ()")) {
                    continue;
                }

                const std::size_t placeholder_pos = (*lines)[line_index].find("function()");
                const std::size_t spaced_placeholder_pos =
                    (*lines)[line_index].find("function ()");
                if (placeholder_pos != std::string::npos) {
                    (*lines)[line_index].replace(placeholder_pos, 10, assign_lhs);
                } else if (spaced_placeholder_pos != std::string::npos) {
                    (*lines)[line_index].replace(spaced_placeholder_pos, 11, assign_lhs);
                } else {
                    continue;
                }

                changed = true;
                pass_changed = true;
                break;
            }

            if (pass_changed) {
                break;
            }
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaResidualFunctionPlaceholdersLate(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    if (!HasRenderedLuaFunctionPlaceholderSignals(*lines)) {
        return false;
    }

    bool changed = false;
    std::string last_empty_local_function_name;
    std::string last_call_result_placeholder_name;

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);

        if (trimmed.starts_with("function ") || trimmed.starts_with("return function(") ||
            trimmed.starts_with("return function ")) {
            last_empty_local_function_name.clear();
            last_call_result_placeholder_name.clear();
        }

        if (trimmed.starts_with("local ")) {
            const std::size_t equal = trimmed.find('=');
            if (equal != std::string_view::npos) {
                const std::string assign_lhs = std::string(
                    TrimLuaWhitespace(trimmed.substr(6, equal - 6)));
                const std::string_view assign_rhs =
                    TrimLuaWhitespace(trimmed.substr(equal + 1));
                if (IsValidLuaIdentifier(assign_lhs) &&
                    (assign_rhs == "function()" || assign_rhs == "function ()")) {
                    std::size_t function_end = line_index + 1;
                    while (function_end < lines->size() &&
                           TrimLuaWhitespace(StripLuaLineComment((*lines)[function_end])).empty()) {
                        ++function_end;
                    }
                    if (function_end < lines->size() &&
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[function_end])) == "end") {
                        last_empty_local_function_name = assign_lhs;
                    }
                }
            }
        }

        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (ParseRenderedAssignmentLineAllowFunctionPlaceholders((*lines)[line_index],
                                                                 &assign_is_local,
                                                                 &lhs_names,
                                                                 &rhs_expression) &&
            lhs_names.size() >= 2) {
            std::string callee;
            std::vector<std::string> arguments;
            if (ParseRenderedSingleCallLine(rhs_expression, &callee, &arguments)) {
                const std::string normalized_callee =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(callee), nullptr);
                bool saw_function_placeholder = false;
                std::vector<std::string> rewritten_lhs = lhs_names;
                std::vector<std::string> function_placeholder_replacements;
                for (std::size_t lhs_index = 0; lhs_index < lhs_names.size(); ++lhs_index) {
                    const std::string normalized_lhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_names[lhs_index]),
                                                      nullptr);
                    if (normalized_lhs == "function()" || normalized_lhs == "function ()") {
                        rewritten_lhs[lhs_index] = "__refii_" + normalized_callee + "_" +
                                                   std::to_string(line_index + 1) + "_" +
                                                   std::to_string(lhs_index + 1);
                        function_placeholder_replacements.push_back(rewritten_lhs[lhs_index]);
                        saw_function_placeholder = true;
                    }
                }

                if (saw_function_placeholder) {
                    const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
                    std::ostringstream rebuilt_line;
                    rebuilt_line << indent << (assign_is_local ? "local " : "");
                    for (std::size_t lhs_index = 0; lhs_index < rewritten_lhs.size(); ++lhs_index) {
                        if (lhs_index != 0) {
                            rebuilt_line << ", ";
                        }
                        rebuilt_line << rewritten_lhs[lhs_index];
                    }
                    rebuilt_line << " = " << rhs_expression;
                    (*lines)[line_index] = rebuilt_line.str();
                    last_call_result_placeholder_name = function_placeholder_replacements.back();
                    changed = true;
                    continue;
                }
            }
        }

        if (!last_empty_local_function_name.empty() &&
            (trimmed.starts_with("function()(") || trimmed.starts_with("function ()("))) {
            const std::size_t placeholder_pos = (*lines)[line_index].find("function()");
            const std::size_t spaced_placeholder_pos =
                (*lines)[line_index].find("function ()");
            if (placeholder_pos != std::string::npos) {
                (*lines)[line_index].replace(placeholder_pos, 10, last_empty_local_function_name);
                changed = true;
                continue;
            }
            if (spaced_placeholder_pos != std::string::npos) {
                (*lines)[line_index].replace(spaced_placeholder_pos,
                                             11,
                                             last_empty_local_function_name);
                changed = true;
                continue;
            }
        }

        if (!last_call_result_placeholder_name.empty()) {
            std::string rewritten_line;
            if (ReplaceRenderedFunctionPlaceholderUsesInLine((*lines)[line_index],
                                                             last_call_result_placeholder_name,
                                                             &rewritten_line) &&
                rewritten_line != (*lines)[line_index]) {
                (*lines)[line_index] = std::move(rewritten_line);
                changed = true;
            }
        }
    }

    return changed;
}

}  // namespace refii::lua
