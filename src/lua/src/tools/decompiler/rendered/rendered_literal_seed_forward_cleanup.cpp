#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {
namespace {

template <typename PassFn>
bool RunLiteralSeedForwardCleanupPassLoop(int max_iterations, PassFn&& run_single_pass) {
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

bool SimplifyRenderedLuaForwardCallArgumentLiteralSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    RunLiteralSeedForwardCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.empty()) {
                continue;
            }

            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            std::vector<std::string> lhs_parts;
            std::vector<std::string> rhs_parts;
            if (!SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(0, equal)),
                                                 &lhs_parts) ||
                !SplitRenderedTopLevelCommaChain(TrimLuaWhitespace(trimmed.substr(equal + 1)),
                                                 &rhs_parts) ||
                lhs_parts.size() != 2 || rhs_parts.size() != 2) {
                continue;
            }

            const std::string lhs0 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[0]), nullptr);
            const std::string lhs1 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_parts[1]), nullptr);
            const std::string rhs0 = std::string(TrimLuaWhitespace(rhs_parts[0]));
            const std::string rhs1 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[1]), nullptr);
            if (!IsRenderTemporaryName(lhs0) || !IsRenderTemporaryName(lhs1) ||
                rhs1.size() < 2 || rhs1.front() != '{' || rhs1.back() != '}') {
                continue;
            }

            if (CountIdentifierUses(rhs0, lhs1) != 1 || CountIdentifierUses(rhs1, lhs0) != 0 ||
                CountIdentifierUses(rhs1, lhs1) != 0) {
                continue;
            }

            const std::string normalized_rhs0 =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs0), nullptr);
            if (normalized_rhs0.find('(') == std::string::npos ||
                normalized_rhs0.find(')') == std::string::npos) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view comment =
                stripped.size() < (*lines)[line_index].size()
                    ? std::string_view((*lines)[line_index]).substr(stripped.size())
                    : std::string_view();

            (*lines)[line_index] = indent + lhs1 + " = " + rhs1;
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                          indent + lhs0 + " = " + normalized_rhs0 + std::string(comment));
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaForwardInvalidTemporaryTableSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    static constexpr std::string_view kInvalidPrefix = "-- refii-invalid: local ";

    bool changed = false;
    RunLiteralSeedForwardCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression) ||
                is_local) {
                continue;
            }

            const std::string temporary_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (!IsRenderTemporaryName(temporary_name)) {
                continue;
            }

            std::size_t invalid_line = lines->size();
            for (std::size_t probe = line_index + 1;
                 probe < lines->size() && probe < line_index + 5;
                 ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    continue;
                }

                const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (trimmed.starts_with(kInvalidPrefix)) {
                    invalid_line = probe;
                }
                break;
            }
            if (invalid_line >= lines->size()) {
                continue;
            }

            const std::string_view trimmed_invalid = TrimLuaWhitespace((*lines)[invalid_line]);
            const std::string expected_prefix =
                std::string(kInvalidPrefix) + temporary_name + " = {";
            if (!trimmed_invalid.starts_with(expected_prefix)) {
                continue;
            }

            bool found_later_local = false;
            const std::size_t later_limit = std::min(lines->size(), invalid_line + 40);
            for (std::size_t probe = invalid_line + 1; probe < later_limit; ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    continue;
                }

                bool probe_local = false;
                std::string probe_lhs_expression;
                std::string probe_rhs_expression;
                if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                       &probe_local,
                                                       &probe_lhs_expression,
                                                       &probe_rhs_expression) ||
                    !probe_local) {
                    continue;
                }

                const std::string normalized_probe_lhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs_expression), nullptr);
                if (normalized_probe_lhs == temporary_name) {
                    found_later_local = true;
                    break;
                }
            }
            if (!found_later_local) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            (*lines)[line_index] = indent + lhs_expression + " = {}";
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(invalid_line));
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaForwardSetlistPairSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_pair_seed_value = [](std::string_view expression) {
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        return !normalized.empty() &&
               (LooksLikeRenderedNumericLiteral(normalized) || normalized == "true" ||
                normalized == "false" || normalized == "nil" ||
                normalized.front() == '"' || normalized.front() == '\'' ||
                IsRenderedSafePrefixExpression(normalized));
    };

    const auto rebuild_literal_assignment = [](std::string_view original_line,
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
            rebuilt += TrimLuaWhitespace(entries[i]);
        }
        rebuilt += "}";
        rebuilt += std::string(comment);
        return rebuilt;
    };

    bool changed = false;
    RunLiteralSeedForwardCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression) ||
                !is_local) {
                continue;
            }

            const std::string temporary_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (!IsRenderTemporaryName(temporary_name) || normalized_rhs != "{}") {
                continue;
            }

            std::size_t banner_begin = lines->size();
            std::size_t banner_end = lines->size();
            bool saw_setlist_banner = false;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    break;
                }

                const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (trimmed.empty()) {
                    if (banner_begin != lines->size()) {
                        banner_end = probe;
                    }
                    continue;
                }

                if (!IsRenderedSetlistErrorBanner((*lines)[probe])) {
                    break;
                }

                if (banner_begin == lines->size()) {
                    banner_begin = probe;
                }
                banner_end = probe;
                saw_setlist_banner =
                    saw_setlist_banner || IsRenderedSetlistMissingListBanner((*lines)[probe]);
            }
            if (banner_begin >= lines->size() || banner_end >= lines->size() ||
                !saw_setlist_banner) {
                continue;
            }

            const std::size_t use_limit = std::min(lines->size(), banner_end + 25);
            for (std::size_t use_line = banner_end + 1; use_line < use_limit; ++use_line) {
                if (use_line >= scope_paths.size() || !scope_paths[use_line].empty()) {
                    continue;
                }

                bool use_is_local = false;
                std::string use_lhs_expression;
                std::string use_rhs_expression;
                if (!ParseRenderedSingleAssignmentLine((*lines)[use_line],
                                                       &use_is_local,
                                                       &use_lhs_expression,
                                                       &use_rhs_expression)) {
                    continue;
                }

                const std::string use_rhs =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(use_rhs_expression), nullptr);
                if (use_rhs.size() < 2 || use_rhs.front() != '{' || use_rhs.back() != '}') {
                    continue;
                }

                std::vector<std::string> entries;
                if (!SplitRenderedTopLevelCommaChain(
                        TrimLuaWhitespace(use_rhs.substr(1, use_rhs.size() - 2)), &entries) ||
                    entries.size() < 3) {
                    continue;
                }

                std::size_t temporary_index = entries.size();
                for (std::size_t i = 0; i < entries.size(); ++i) {
                    const std::string normalized_entry =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(entries[i]), nullptr);
                    if (normalized_entry == temporary_name) {
                        if (temporary_index != entries.size()) {
                            temporary_index = entries.size();
                            break;
                        }
                        temporary_index = i;
                    }
                }
                if (temporary_index == entries.size() || temporary_index + 2 >= entries.size()) {
                    continue;
                }

                if (!is_pair_seed_value(entries[temporary_index + 1]) ||
                    !is_pair_seed_value(entries[temporary_index + 2]) ||
                    CountIdentifierUses(entries[temporary_index + 1], temporary_name) != 0 ||
                    CountIdentifierUses(entries[temporary_index + 2], temporary_name) != 0) {
                    continue;
                }

                const std::string seed_indent(GetRenderedLuaIndent((*lines)[line_index]));
                (*lines)[line_index] = seed_indent + "local " + temporary_name + " = {" +
                                       std::string(TrimLuaWhitespace(entries[temporary_index + 1])) +
                                       ", " +
                                       std::string(TrimLuaWhitespace(entries[temporary_index + 2])) +
                                       "}";

                entries.erase(entries.begin() +
                                  static_cast<std::ptrdiff_t>(temporary_index + 1),
                              entries.begin() +
                                  static_cast<std::ptrdiff_t>(temporary_index + 3));
                (*lines)[use_line] = rebuild_literal_assignment((*lines)[use_line],
                                                                use_is_local,
                                                                use_lhs_expression,
                                                                entries);
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(banner_begin),
                             lines->begin() + static_cast<std::ptrdiff_t>(banner_end + 1));
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

}  // namespace refii::lua
