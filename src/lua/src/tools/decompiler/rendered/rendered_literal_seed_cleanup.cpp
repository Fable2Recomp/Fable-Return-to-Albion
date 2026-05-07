#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {
namespace {

template <typename PassFn>
bool RunLiteralSeedCleanupPassLoop(int max_iterations, PassFn&& run_single_pass) {
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

bool SimplifyRenderedLuaImmediateLocalLiteralSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_simple_literal_seed = [](std::string_view expression) {
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        return normalized == "nil" || normalized == "true" || normalized == "false" ||
               LooksLikeRenderedNumericLiteral(normalized) ||
               (!normalized.empty() &&
                (normalized.front() == '"' || normalized.front() == '\''));
    };
    const auto next_nonempty_index = [&](std::size_t start) -> std::size_t {
        std::size_t index = start;
        while (index < lines->size() &&
               TrimLuaWhitespace(StripLuaLineComment((*lines)[index])).empty()) {
            ++index;
        }
        return index;
    };

    bool changed = false;
    RunLiteralSeedCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size()) {
                continue;
            }

            bool seed_is_local = false;
            std::string seed_lhs;
            std::string seed_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &seed_is_local,
                                                   &seed_lhs,
                                                   &seed_rhs) ||
                !seed_is_local) {
                continue;
            }

            const std::string normalized_lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_lhs), nullptr);
            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
            if (!IsRenderTemporaryName(normalized_lhs) || !is_simple_literal_seed(normalized_rhs)) {
                continue;
            }

            const std::size_t use_index = next_nonempty_index(line_index + 1);
            if (use_index >= lines->size() || use_index >= scope_paths.size() ||
                !IsRenderedScopePathPrefix(scope_paths[line_index], scope_paths[use_index])) {
                continue;
            }
            if (CountIdentifierUses((*lines)[use_index], normalized_lhs) != 1) {
                continue;
            }

            bool used_later = false;
            for (std::size_t probe = use_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() ||
                    !IsRenderedScopePathPrefix(scope_paths[line_index], scope_paths[probe])) {
                    continue;
                }
                if (IsAssignmentToIdentifier((*lines)[probe], normalized_lhs)) {
                    break;
                }
                if (CountIdentifierUses((*lines)[probe], normalized_lhs) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (used_later) {
                continue;
            }

            std::string rewritten;
            if (!ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine((*lines)[use_index],
                                                                             normalized_lhs,
                                                                             normalized_rhs,
                                                                             &rewritten) ||
                rewritten.empty() || rewritten == (*lines)[use_index]) {
                continue;
            }

            (*lines)[use_index] = std::move(rewritten);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaForwardLocalLiteralSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_simple_literal_seed = [](std::string_view expression) {
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        return normalized == "nil" || normalized == "true" || normalized == "false" ||
               LooksLikeRenderedNumericLiteral(normalized) ||
               (!normalized.empty() &&
                (normalized.front() == '"' || normalized.front() == '\''));
    };
    const auto trim_rendered_trailing_comma = [](std::string_view expression) {
        expression = TrimLuaWhitespace(expression);
        if (!expression.empty() && expression.back() == ',') {
            expression = TrimLuaWhitespace(expression.substr(0, expression.size() - 1));
        }
        return StripRenderedWholeParentheses(expression, nullptr);
    };

    bool changed = false;
    RunLiteralSeedCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 1; line_index < lines->size(); ++line_index) {
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
            const bool is_simple_seed = is_simple_literal_seed(normalized_rhs);
            const bool is_table_literal_seed =
                !normalized_rhs.empty() && normalized_rhs.front() == '{';
            if (!IsRenderTemporaryName(normalized_lhs) ||
                (!is_simple_seed && !is_table_literal_seed)) {
                continue;
            }

            const std::size_t assignment_end =
                is_table_literal_seed ? FindRenderedTableLiteralEnd(*lines, line_index)
                                      : line_index;
            if (assignment_end >= lines->size()) {
                continue;
            }

            std::size_t literal_end = line_index;
            while (literal_end > 0) {
                --literal_end;
                if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[literal_end])).empty()) {
                    break;
                }
            }

            const std::string_view end_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[literal_end]));
            if (end_trimmed != "}" && end_trimmed != "},") {
                continue;
            }

            std::size_t literal_start = lines->size();
            for (std::size_t probe = literal_end + 1; probe > 0; --probe) {
                const std::size_t candidate = probe - 1;
                if (candidate >= scope_paths.size() || line_index >= scope_paths.size() ||
                    scope_paths[candidate] != scope_paths[line_index]) {
                    continue;
                }
                std::string indent;
                std::string lhs_prefix;
                std::string table_name;
                if (!ParseRenderedTableLiteralStartLine((*lines)[candidate],
                                                        &indent,
                                                        &lhs_prefix,
                                                        &table_name)) {
                    continue;
                }
                if (FindRenderedTableLiteralEnd(*lines, candidate) == literal_end) {
                    literal_start = candidate;
                    break;
                }
            }
            if (literal_start >= lines->size()) {
                continue;
            }

            std::size_t block_use_count = 0;
            std::size_t use_line = lines->size();
            for (std::size_t probe = literal_start; probe <= literal_end; ++probe) {
                const std::size_t line_use_count =
                    CountIdentifierUses((*lines)[probe], normalized_lhs);
                block_use_count += line_use_count;
                if (line_use_count != 0) {
                    use_line = probe;
                }
            }
            if (block_use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            if (is_table_literal_seed) {
                bool use_is_local = false;
                std::string use_lhs_expression;
                std::string use_rhs_expression;
                if (!ParseRenderedSingleAssignmentLine((*lines)[use_line],
                                                       &use_is_local,
                                                       &use_lhs_expression,
                                                       &use_rhs_expression) ||
                    trim_rendered_trailing_comma(use_rhs_expression) != normalized_lhs) {
                    continue;
                }
            }

            bool failed = false;
            for (std::size_t probe = literal_end + 1; probe < line_index; ++probe) {
                if (probe >= scope_paths.size() || line_index >= scope_paths.size() ||
                    scope_paths[probe] != scope_paths[line_index]) {
                    failed = true;
                    break;
                }
                if (CountIdentifierUses((*lines)[probe], normalized_lhs) != 0) {
                    failed = true;
                    break;
                }
                if (is_table_literal_seed &&
                    !TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                    failed = true;
                    break;
                }
            }
            if (failed) {
                continue;
            }

            if (is_table_literal_seed) {
                std::vector<std::string> moved_assignment_lines(
                    lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines->begin() + static_cast<std::ptrdiff_t>(assignment_end + 1));
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                             lines->begin() + static_cast<std::ptrdiff_t>(assignment_end + 1));
                lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(literal_start),
                              moved_assignment_lines.begin(),
                              moved_assignment_lines.end());
                changed = true;
                pass_changed = true;
                break;
            }

            bool replaced_any = false;
            for (std::size_t probe = literal_start; probe <= literal_end; ++probe) {
                if (CountIdentifierUses((*lines)[probe], normalized_lhs) == 0) {
                    continue;
                }

                std::string rewritten;
                if (!ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine((*lines)[probe],
                                                                                 normalized_lhs,
                                                                                 normalized_rhs,
                                                                                 &rewritten) ||
                    rewritten.empty()) {
                    failed = true;
                    break;
                }
                (*lines)[probe] = std::move(rewritten);
                replaced_any = true;
            }

            if (failed || !replaced_any) {
                continue;
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
