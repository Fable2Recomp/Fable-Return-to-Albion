#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <initializer_list>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace refii::lua {

namespace {

std::uint64_t BuildRenderedLuaLinesStateKey(const std::vector<std::string>& lines) {
    std::uint64_t hash = 1469598103934665603ull;
    const auto mix = [&](unsigned char byte) {
        hash ^= static_cast<std::uint64_t>(byte);
        hash *= 1099511628211ull;
    };

    for (const std::string& line : lines) {
        mix(static_cast<unsigned char>(line.size() & 0xffu));
        mix(static_cast<unsigned char>((line.size() >> 8) & 0xffu));
        for (unsigned char byte : line) {
            mix(byte);
        }
        mix(static_cast<unsigned char>('\n'));
    }
    return hash;
}

bool RunRenderedCleanupFixpoint(
    std::vector<std::string>* lines,
    const std::function<bool(std::vector<std::string>*)>& pass,
    int max_iterations = 6,
    bool detect_state_cycles = true) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    std::unordered_set<std::uint64_t> seen_states;
    std::uint64_t current_state = 0;
    if (detect_state_cycles) {
        seen_states.reserve(static_cast<std::size_t>(std::max(max_iterations + 1, 2)));
        current_state = BuildRenderedLuaLinesStateKey(*lines);
        seen_states.insert(current_state);
    }

    for (int iteration = 0; iteration < max_iterations; ++iteration) {
        if (!pass(lines)) {
            break;
        }
        changed = true;
        if (detect_state_cycles) {
            const std::uint64_t next_state = BuildRenderedLuaLinesStateKey(*lines);
            if (next_state == current_state) {
                break;
            }
            if (!seen_states.insert(next_state).second) {
                break;
            }
            current_state = next_state;
        }
    }
    return changed;
}

using RenderedLuaCleanupStepFn = bool (*)(std::vector<std::string>* lines);

bool ApplyRenderedLuaCleanupStepSequence(
    std::vector<std::string>* lines,
    std::initializer_list<RenderedLuaCleanupStepFn> steps) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (RenderedLuaCleanupStepFn step : steps) {
        if (step != nullptr) {
            changed |= step(lines);
        }
    }
    return changed;
}

bool TryParseRenderedNearExactGlobalSeedName(std::string_view expression,
                                             std::string* global_name_out) {
    if (global_name_out == nullptr) {
        return false;
    }
    *global_name_out = std::string();
    expression = refii::lua::TrimLuaWhitespace(expression);
    if (expression.empty()) {
        return false;
    }

    if (expression.starts_with("_G.")) {
        const std::string_view dotted = refii::lua::TrimLuaWhitespace(expression.substr(3));
        if (refii::lua::IsValidLuaIdentifier(dotted)) {
            *global_name_out = std::string(dotted);
            return true;
        }
        return false;
    }

    if (!expression.starts_with("_G[") || !expression.ends_with(']') ||
        expression.size() < 6) {
        return false;
    }
    const std::string_view inner =
        refii::lua::TrimLuaWhitespace(expression.substr(3, expression.size() - 4));
    if (inner.size() < 2) {
        return false;
    }
    const char quote = inner.front();
    if ((quote != '"' && quote != '\'') || inner.back() != quote) {
        return false;
    }
    const std::string_view unquoted = inner.substr(1, inner.size() - 2);
    if (!refii::lua::IsValidLuaIdentifier(unquoted)) {
        return false;
    }
    *global_name_out = std::string(unquoted);
    return true;
}

}  // namespace

bool ComposeRenderedLuaValidatedMemberAliasCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            bool changed = false;
            changed |= refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaScopedMemberAliases(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaTopLevelMemberAliases(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaRedundantLocalRedeclarations(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaTemporaryIdentifierCarries(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaImmediateBooleanAliasCarries(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaNearbyBooleanAliasLocals(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaEscapingBooleanAliasUses(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments(
                local_lines);
            return changed;
        });
}

bool ComposeRenderedLuaQualifiedTableBuilderCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            bool changed = false;
            changed |=
                refii::lua::ExpandRenderedLuaQualifiedTablePlaceholderLiterals(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaQualifiedTableLiteralArrayTails(
                local_lines);
            changed |= refii::lua::SimplifyRenderedLuaQualifiedTableBuilders(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaDelayedArrayTableBuilders(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaNestedTableLiteralPlaceholders(
                local_lines);
            changed |= refii::lua::NormalizeRenderedLuaNilTablePlaceholders(local_lines);
            return changed;
        },
        4);
}

bool ComposeRenderedLuaOrderedCompareSelfCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            bool changed = ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::SimplifyRenderedLuaLoopTrailingReturnElse,
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments,
                    refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries,
                    refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals,
                    refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals,
                    refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals,
                    refii::lua::SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals,
                    refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                });
            changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(local_lines);
            return changed;
        },
        6,
        true);
}

bool ComposeRenderedLuaConditionTempGuardCleanupTail(std::vector<std::string>* lines,
                                                     bool include_table_builder_argument_cleanup,
                                                     bool include_compare_swap_cleanup) {
    return RunRenderedCleanupFixpoint(
        lines,
        [&](std::vector<std::string>* local_lines) -> bool {
            bool changed = ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::LocalizeRenderedLuaFirstAssignments,
                    refii::lua::LocalizeRenderedLuaFunctionLocals,
                    refii::lua::SimplifyRenderedLuaBranchShadowedLocals,
                    refii::lua::UnwrapRenderedLuaReturnedCallsForExactParity,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                    refii::lua::CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity,
                    refii::lua::RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
                    refii::lua::CollapseRenderedLuaNestedSharedElseGuardsForExactParity,
                    refii::lua::SimplifyRenderedLuaTableBuilders,
                });
            if (include_table_builder_argument_cleanup) {
                changed |= refii::lua::SimplifyRenderedLuaLocalTableBuilderCallArguments(
                    local_lines);
                changed |=
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryCallArgumentUses(
                        local_lines);
            }
            if (include_compare_swap_cleanup) {
                changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(
                    local_lines);
            }
            changed |= refii::lua::SimplifyRenderedLuaTableInitializerTemporaries(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaTableLiteralTemporaries(local_lines);
            changed |=
                refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments(local_lines);
            return changed;
        },
        6,
        true);
}

bool ComposeRenderedLuaConditionTempGuardCleanupTailForNearExact(
    std::vector<std::string>* lines) {
    return ComposeRenderedLuaConditionTempGuardCleanupTail(
        lines,
        /*include_table_builder_argument_cleanup=*/true,
        /*include_compare_swap_cleanup=*/true);
}

bool ComposeRenderedLuaConditionTempGuardCleanupTailForValidated(
    std::vector<std::string>* lines) {
    return ComposeRenderedLuaConditionTempGuardCleanupTail(
        lines,
        /*include_table_builder_argument_cleanup=*/false,
        /*include_compare_swap_cleanup=*/false);
}

bool ComposeRenderedLuaSmallCallCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            bool changed = ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::SimplifyRenderedLuaImmediateSingleUseGuardTemps,
                    refii::lua::SimplifyRenderedLuaNestedAssignedGuardChains,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments,
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryCallArgumentUses,
                    refii::lua::SimplifyRenderedLuaNearbyAssignedRepeatedConditions,
                    refii::lua::SimplifyRenderedLuaNearbyAssignedStatementChainUses,
                    refii::lua::SimplifyRenderedLuaImmediateRedundantConditionTemps,
                    refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps,
                    refii::lua::SimplifyRenderedLuaNestedGuardedConditionChains,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                });
            changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(local_lines);
            return changed;
        },
        6,
        true);
}

bool ComposeRenderedLuaAssignedGuardCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            bool changed = ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::SimplifyRenderedLuaImmediateSingleUseGuardTemps,
                    refii::lua::SimplifyRenderedLuaNestedAssignedGuardChains,
                    refii::lua::SimplifyRenderedLuaImmediateAssignedRepeatedConditions,
                    refii::lua::SimplifyRenderedLuaNearbyAssignedRepeatedConditions,
                    refii::lua::SimplifyRenderedLuaImmediateAssignedConditionExpressionUses,
                    refii::lua::SimplifyRenderedLuaNearbyAssignedStatementChainUses,
                    refii::lua::SimplifyRenderedLuaImmediateRedundantConditionTemps,
                    refii::lua::SimplifyRenderedLuaImmediateAssignedCallGuards,
                    refii::lua::SimplifyRenderedLuaImmediateAssignedMethodCallGuards,
                    refii::lua::SimplifyRenderedLuaImmediateAssignedRepeatedCallLoops,
                    refii::lua::SimplifyRenderedLuaRepeatedCallGuards,
                    refii::lua::SimplifyRenderedLuaNestedGuardedConditionChains,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments,
                    refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps,
                });
            changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(local_lines);
            return changed;
        });
}

bool ComposeRenderedLuaMemberAliasCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            return ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries,
                    refii::lua::SimplifyRenderedLuaScopedMemberAliases,
                    refii::lua::SimplifyRenderedLuaTopLevelMemberAliases,
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryCallArgumentUses,
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                });
        });
}

bool ComposeRenderedLuaNamespaceExportAliasCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            bool changed = ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::SimplifyRenderedLuaTopLevelNamespaceTableAliasChains,
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments,
                    refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                });
            changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(local_lines);
            return changed;
        });
}

bool ComposeRenderedLuaTopLevelAliasCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            bool changed = ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::SimplifyRenderedLuaTopLevelNamespaceTableAliasChains,
                    refii::lua::SimplifyRenderedLuaTopLevelObjectCarryAssignments,
                    refii::lua::SimplifyRenderedLuaKnownFieldCarryAssignments,
                    refii::lua::SimplifyRenderedLuaTopLevelImmediateResultAliases,
                    refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries,
                    refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals,
                    refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals,
                    refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals,
                    refii::lua::SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals,
                    refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries,
                    refii::lua::SimplifyRenderedLuaScopedMemberAliases,
                    refii::lua::SimplifyRenderedLuaTopLevelMemberAliases,
                    refii::lua::SimplifyRenderedLuaTopLevelFunctionAliasAssignments,
                    refii::lua::SimplifyRenderedLuaImmediateTemporaryAssignments,
                    refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                    refii::lua::ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity,
                    refii::lua::ExpandRenderedLuaOrTableFallbackAssignmentsForParity,
                    refii::lua::ExpandRenderedLuaInlineLocalTableSeedFieldsForParity,
                });
            changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(local_lines);
            return changed;
        });
}

bool ComposeRenderedLuaPredicateReturnParityCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            return ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::SimplifyRenderedLuaConditionHeaders,
                    refii::lua::SimplifyRenderedLuaRedundantGuardConditionParentheses,
                    refii::lua::HoistRenderedLuaSingleWhileElseTailsForExactParity,
                    refii::lua::CollapseRenderedLuaNestedSharedElseGuardsForExactParity,
                    refii::lua::CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity,
                    refii::lua::RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
                    refii::lua::ExpandRenderedLuaPredicateBooleanReturnsForParity,
                    refii::lua::UnwrapRenderedLuaReturnedCallsForExactParity,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                });
        });
}

bool ComposeRenderedLuaNearExactCommitParityCleanupTail(std::vector<std::string>* lines) {
    return RunRenderedCleanupFixpoint(
        lines,
        [](std::vector<std::string>* local_lines) -> bool {
            bool changed = false;
            changed |= refii::lua::NormalizeRenderedLuaNearExactHeadGlobalSeeds(local_lines);
            changed |= ApplyRenderedLuaCleanupStepSequence(
                local_lines,
                {
                    refii::lua::SimplifyRenderedLuaConditionHeaders,
                    refii::lua::SimplifyRenderedLuaRedundantGuardConditionParentheses,
                    refii::lua::SimplifyRenderedLuaBooleanLoadPairs,
                    refii::lua::SimplifyRenderedLuaBooleanCarryConditions,
                    refii::lua::SimplifyRenderedLuaImmediateAssignedConditionExpressionUses,
                    refii::lua::SimplifyRenderedLuaTopLevelNamespaceTableAliasChains,
                    refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries,
                    refii::lua::SimplifyRenderedLuaScopedMemberAliases,
                    refii::lua::SimplifyRenderedLuaTopLevelMemberAliases,
                    refii::lua::SimplifyRenderedLuaTopLevelFunctionAliasAssignments,
                    refii::lua::SimplifyRenderedLuaTopLevelImmediateResultAliases,
                    refii::lua::CollapseRenderedLuaNestedSharedElseGuardsForExactParity,
                    refii::lua::CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity,
                    refii::lua::RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity,
                    refii::lua::SimplifyRenderedLuaAdjacentDuplicateCallValueConditions,
                    refii::lua::SimplifyRenderedLuaRepeatedCallGuards,
                    refii::lua::SimplifyRenderedLuaRepeatedCallResultGuards,
                    refii::lua::LocalizeRenderedLuaFirstAssignments,
                    refii::lua::LocalizeRenderedLuaFunctionLocals,
                    refii::lua::SimplifyRenderedLuaRedundantLocalRedeclarations,
                    refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries,
                    refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals,
                    refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals,
                    refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals,
                    refii::lua::SimplifyRenderedLuaExplicitSelfMethodArguments,
                    refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments,
                    refii::lua::SimplifyRenderedLuaMethodArg0SelfParameters,
                    refii::lua::SimplifyRenderedLuaNestedGuardedDuplicateBodies,
                    refii::lua::SimplifyRenderedLuaNestedGuardedFallbackDuplicateBodies,
                });
            changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(local_lines);
            changed |= refii::lua::SimplifyRenderedLuaStraightLineSingleUseTemps(local_lines);
            return changed;
        });
}

bool TrySwapRenderedCallRightHandComparisonsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_top_level_comparison =
        [](std::string_view value, std::size_t* position_out, std::string* comparator_out) {
            if (position_out == nullptr || comparator_out == nullptr) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            for (std::size_t index = 0; index < value.size(); ++index) {
                const char ch = value[index];
                if (ch == '\'' && !in_double_quote) {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (ch == '"' && !in_single_quote) {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    --paren_depth;
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    --brace_depth;
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    --bracket_depth;
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }

                for (std::string_view needle :
                     {" <= ", " >= ", " ~= ", " == ", " < ", " > "}) {
                    if (value.substr(index).starts_with(needle)) {
                        *position_out = index;
                        *comparator_out = std::string(needle);
                        return true;
                    }
                }
            }
            return false;
        };
    const auto swap_comparator = [](std::string_view comparator) -> std::string {
        if (comparator == " < ") {
            return " > ";
        }
        if (comparator == " > ") {
            return " < ";
        }
        if (comparator == " <= ") {
            return " >= ";
        }
        if (comparator == " >= ") {
            return " <= ";
        }
        return std::string(comparator);
    };
    const auto strip_whole_parentheses =
        [](std::string_view value, bool* changed) -> std::string {
            const auto is_whole_parenthesized = [](std::string_view text) {
                text = refii::lua::TrimLuaWhitespace(text);
                if (text.size() < 2 || text.front() != '(' || text.back() != ')') {
                    return false;
                }

                bool in_single_quote = false;
                bool in_double_quote = false;
                int paren_depth = 0;
                int brace_depth = 0;
                int bracket_depth = 0;
                for (std::size_t i = 0; i < text.size(); ++i) {
                    const char ch = text[i];
                    if (in_single_quote) {
                        if (ch == '\\' && i + 1 < text.size()) {
                            ++i;
                        } else if (ch == '\'') {
                            in_single_quote = false;
                        }
                        continue;
                    }
                    if (in_double_quote) {
                        if (ch == '\\' && i + 1 < text.size()) {
                            ++i;
                        } else if (ch == '"') {
                            in_double_quote = false;
                        }
                        continue;
                    }
                    if (ch == '\'') {
                        in_single_quote = true;
                        continue;
                    }
                    if (ch == '"') {
                        in_double_quote = true;
                        continue;
                    }
                    if (ch == '(') {
                        ++paren_depth;
                        continue;
                    }
                    if (ch == ')') {
                        --paren_depth;
                        if (paren_depth == 0) {
                            return i + 1 == text.size();
                        }
                        if (paren_depth < 0) {
                            return false;
                        }
                        continue;
                    }
                    if (ch == '{') {
                        ++brace_depth;
                        continue;
                    }
                    if (ch == '}') {
                        --brace_depth;
                        continue;
                    }
                    if (ch == '[') {
                        ++bracket_depth;
                        continue;
                    }
                    if (ch == ']') {
                        --bracket_depth;
                        continue;
                    }
                }

                return false;
            };

            value = refii::lua::TrimLuaWhitespace(value);
            while (is_whole_parenthesized(value)) {
                value = refii::lua::TrimLuaWhitespace(value.substr(1, value.size() - 2));
                if (changed != nullptr) {
                    *changed = true;
                }
            }
            return std::string(value);
        };
    const auto try_parse_condition_header =
        [](std::string_view line,
           std::string* prefix,
           std::string* condition,
           std::string* suffix) {
            if (prefix == nullptr || condition == nullptr || suffix == nullptr) {
                return false;
            }

            const std::string indent =
                std::string(line.substr(0, line.find_first_not_of(" \t")));
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                *prefix = indent + "if ";
                *condition = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
                *suffix = " then";
                return true;
            }
            if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                *prefix = indent + "elseif ";
                *condition = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
                *suffix = " then";
                return true;
            }
            return false;
        };
    const auto split_top_level_boolean_chain =
        [](std::string_view value,
           std::string_view separator,
           std::vector<std::string>* parts_out) -> bool {
            if (parts_out == nullptr) {
                return false;
            }

            parts_out->clear();
            value = refii::lua::TrimLuaWhitespace(value);
            if (value.empty()) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t part_start = 0;
            bool found_separator = false;
            for (std::size_t index = 0; index < value.size(); ++index) {
                const char ch = value[index];
                if (in_single_quote) {
                    if (ch == '\\' && index + 1 < value.size()) {
                        ++index;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && index + 1 < value.size()) {
                        ++index;
                    } else if (ch == '"') {
                        in_double_quote = false;
                    }
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = true;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = true;
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    --paren_depth;
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    --brace_depth;
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    --bracket_depth;
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }

                if (index + separator.size() > value.size() ||
                    value.substr(index, separator.size()) != separator) {
                    continue;
                }

                const bool boundary_before =
                    index == 0 ||
                    std::isspace(static_cast<unsigned char>(value[index - 1])) != 0;
                const bool boundary_after =
                    index + separator.size() >= value.size() ||
                    std::isspace(static_cast<unsigned char>(value[index + separator.size()])) != 0;
                if (!boundary_before || !boundary_after) {
                    continue;
                }

                const std::string part = std::string(
                    refii::lua::TrimLuaWhitespace(value.substr(part_start, index - part_start)));
                if (part.empty()) {
                    parts_out->clear();
                    return false;
                }
                parts_out->push_back(part);
                part_start = index + separator.size();
                found_separator = true;
                index += separator.size() - 1;
            }

            if (!found_separator) {
                return false;
            }

            const std::string tail =
                std::string(refii::lua::TrimLuaWhitespace(value.substr(part_start)));
            if (tail.empty()) {
                parts_out->clear();
                return false;
            }
            parts_out->push_back(tail);
            return parts_out->size() > 1;
        };
    const auto looks_like_numeric_literal = [](std::string_view value) {
        value = refii::lua::TrimLuaWhitespace(value);
        if (value.empty()) {
            return false;
        }

        if (value.front() == '+' || value.front() == '-') {
            value.remove_prefix(1);
        }
        if (value.empty()) {
            return false;
        }

        bool saw_digit = false;
        bool saw_decimal = false;
        for (const char ch : value) {
            if (std::isdigit(static_cast<unsigned char>(ch)) != 0) {
                saw_digit = true;
                continue;
            }
            if (ch == '.' && !saw_decimal) {
                saw_decimal = true;
                continue;
            }
            return false;
        }
        return saw_digit;
    };
    const auto normalize_comparison_condition =
        [&](std::string_view expression,
            const auto& self) -> std::string {
            const std::string trimmed =
                std::string(refii::lua::TrimLuaWhitespace(expression));
            if (trimmed.empty()) {
                return trimmed;
            }

            std::vector<std::string> parts;
            if (split_top_level_boolean_chain(trimmed, "or", &parts) &&
                parts.size() > 1) {
                std::string rebuilt;
                for (std::size_t index = 0; index < parts.size(); ++index) {
                    if (index != 0) {
                        rebuilt += " or ";
                    }
                    rebuilt += self(parts[index], self);
                }
                return rebuilt;
            }

            parts.clear();
            if (split_top_level_boolean_chain(trimmed, "and", &parts) &&
                parts.size() > 1) {
                std::string rebuilt;
                for (std::size_t index = 0; index < parts.size(); ++index) {
                    if (index != 0) {
                        rebuilt += " and ";
                    }
                    rebuilt += self(parts[index], self);
                }
                return rebuilt;
            }

            bool condition_was_wrapped = false;
            const std::string normalized =
                strip_whole_parentheses(trimmed, &condition_was_wrapped);

            std::size_t compare_position = std::string::npos;
            std::string comparator;
            if (!find_top_level_comparison(normalized, &compare_position, &comparator)) {
                return trimmed;
            }

            const std::string lhs = std::string(refii::lua::TrimLuaWhitespace(
                std::string_view(normalized).substr(0, compare_position)));
            const std::string rhs = std::string(refii::lua::TrimLuaWhitespace(
                std::string_view(normalized).substr(compare_position + comparator.size())));
            if (lhs.empty() || rhs.empty() || lhs == rhs || comparator == " == " ||
                comparator == " ~= " || !looks_like_numeric_literal(lhs) ||
                looks_like_numeric_literal(rhs)) {
                return trimmed;
            }

            std::string swapped = rhs + swap_comparator(comparator) + lhs;
            if (condition_was_wrapped) {
                swapped = "(" + swapped + ")";
            }
            return swapped;
        };

    bool changed = false;
    for (std::string& line : *lines) {
        std::string prefix;
        std::string condition;
        std::string suffix;
        if (!try_parse_condition_header(line, &prefix, &condition, &suffix)) {
            continue;
        }

        const std::string normalized_condition =
            normalize_comparison_condition(condition, normalize_comparison_condition);
        if (normalized_condition == condition) {
            continue;
        }

        line = prefix + normalized_condition + suffix;
        changed = true;
    }

    return changed;
}

bool TrySwapRenderedLeadingCallComparisonsForExactParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_top_level_comparison =
        [](std::string_view value, std::size_t* position_out, std::string* comparator_out) {
            if (position_out == nullptr || comparator_out == nullptr) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            for (std::size_t index = 0; index < value.size(); ++index) {
                const char ch = value[index];
                if (ch == '\'' && !in_double_quote) {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (ch == '"' && !in_single_quote) {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    --paren_depth;
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    --brace_depth;
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    --bracket_depth;
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }

                for (std::string_view needle :
                     {" <= ", " >= ", " ~= ", " == ", " < ", " > "}) {
                    if (value.substr(index).starts_with(needle)) {
                        *position_out = index;
                        *comparator_out = std::string(needle);
                        return true;
                    }
                }
            }
            return false;
        };
    const auto swap_comparator = [](std::string_view comparator) -> std::string {
        if (comparator == " < ") {
            return " > ";
        }
        if (comparator == " > ") {
            return " < ";
        }
        if (comparator == " <= ") {
            return " >= ";
        }
        if (comparator == " >= ") {
            return " <= ";
        }
        return std::string(comparator);
    };
    const auto try_parse_condition_header =
        [](std::string_view line,
           std::string* prefix,
           std::string* condition,
           std::string* suffix) {
            if (prefix == nullptr || condition == nullptr || suffix == nullptr) {
                return false;
            }

            const std::string indent =
                std::string(line.substr(0, line.find_first_not_of(" \t")));
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                *prefix = indent + "if ";
                *condition = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
                *suffix = " then";
                return true;
            }
            if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                *prefix = indent + "elseif ";
                *condition = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
                *suffix = " then";
                return true;
            }
            return false;
        };
    const auto looks_like_call_expression = [](std::string_view value) {
        value = refii::lua::TrimLuaWhitespace(value);
        if (value.starts_with("not ") || value.find(',') != std::string_view::npos) {
            return false;
        }
        return value.find('(') != std::string_view::npos && value.ends_with(")");
    };
    const auto looks_stable_rhs = [](std::string_view value) {
        value = refii::lua::TrimLuaWhitespace(value);
        if (value.empty() || value.starts_with("not ") ||
            value.find(',') != std::string_view::npos ||
            value.find('(') != std::string_view::npos) {
            return false;
        }
        for (std::string_view op : {" and ", " or ", " + ", " - ", " * ", " / "}) {
            if (value.find(op) != std::string_view::npos) {
                return false;
            }
        }
        return true;
    };

    bool changed = false;
    for (std::string& line : *lines) {
        std::string prefix;
        std::string condition;
        std::string suffix;
        if (!try_parse_condition_header(line, &prefix, &condition, &suffix)) {
            continue;
        }

        std::size_t compare_position = std::string::npos;
        std::string comparator;
        if (!find_top_level_comparison(condition, &compare_position, &comparator)) {
            continue;
        }

        const std::string lhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(0, compare_position)));
        const std::string rhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(compare_position + comparator.size())));
        if (!looks_like_call_expression(lhs) || !looks_stable_rhs(rhs)) {
            continue;
        }

        line = prefix + rhs + swap_comparator(comparator) + lhs + suffix;
        changed = true;
    }

    return changed;
}

bool TrySwapRenderedStableRelationalComparisonsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_top_level_relational =
        [](std::string_view value, std::size_t* position_out, std::string* comparator_out) {
            if (position_out == nullptr || comparator_out == nullptr) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            for (std::size_t index = 0; index < value.size(); ++index) {
                const char ch = value[index];
                if (ch == '\'' && !in_double_quote) {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (ch == '"' && !in_single_quote) {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    --paren_depth;
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    --brace_depth;
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    --bracket_depth;
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }

                for (std::string_view needle : {" <= ", " >= ", " < ", " > "}) {
                    if (value.substr(index).starts_with(needle)) {
                        *position_out = index;
                        *comparator_out = std::string(needle);
                        return true;
                    }
                }
            }
            return false;
        };
    const auto swap_comparator = [](std::string_view comparator) -> std::string {
        if (comparator == " < ") {
            return " > ";
        }
        if (comparator == " > ") {
            return " < ";
        }
        if (comparator == " <= ") {
            return " >= ";
        }
        if (comparator == " >= ") {
            return " <= ";
        }
        return std::string(comparator);
    };
    const auto try_parse_condition_header =
        [](std::string_view line,
           std::string* prefix,
           std::string* condition,
           std::string* suffix) {
            if (prefix == nullptr || condition == nullptr || suffix == nullptr) {
                return false;
            }

            const std::string indent =
                std::string(line.substr(0, line.find_first_not_of(" \t")));
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                *prefix = indent + "if ";
                *condition = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
                *suffix = " then";
                return true;
            }
            if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                *prefix = indent + "elseif ";
                *condition = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
                *suffix = " then";
                return true;
            }
            return false;
        };
    const auto stable_operand = [](std::string_view value) {
        value = refii::lua::TrimLuaWhitespace(value);
        if (value.empty() || value.starts_with("not ") ||
            value.find(',') != std::string_view::npos ||
            value.find('(') != std::string_view::npos ||
            value.find('{') != std::string_view::npos) {
            return false;
        }
        for (std::string_view op : {" and ", " or ", " + ", " - ", " * ", " / "}) {
            if (value.find(op) != std::string_view::npos) {
                return false;
            }
        }
        return true;
    };

    bool changed = false;
    for (std::string& line : *lines) {
        std::string prefix;
        std::string condition;
        std::string suffix;
        if (!try_parse_condition_header(line, &prefix, &condition, &suffix)) {
            continue;
        }

        std::size_t compare_position = std::string::npos;
        std::string comparator;
        if (!find_top_level_relational(condition, &compare_position, &comparator)) {
            continue;
        }

        const std::string lhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(0, compare_position)));
        const std::string rhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(compare_position + comparator.size())));
        if (!stable_operand(lhs) || !stable_operand(rhs)) {
            continue;
        }

        line = prefix + rhs + swap_comparator(comparator) + lhs + suffix;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaTimingRelationalCallOrderForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_top_level_relational =
        [](std::string_view value, std::size_t* position_out, std::string* comparator_out) {
            if (position_out == nullptr || comparator_out == nullptr) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            for (std::size_t index = 0; index < value.size(); ++index) {
                const char ch = value[index];
                if (ch == '\'' && !in_double_quote) {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (ch == '"' && !in_single_quote) {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                    continue;
                }
                if (ch == ')') {
                    --paren_depth;
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                    continue;
                }
                if (ch == '}') {
                    --brace_depth;
                    continue;
                }
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    --bracket_depth;
                    continue;
                }
                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                    continue;
                }

                for (std::string_view needle : {" <= ", " >= ", " < ", " > "}) {
                    if (value.substr(index).starts_with(needle)) {
                        *position_out = index;
                        *comparator_out = std::string(needle);
                        return true;
                    }
                }
            }
            return false;
        };
    const auto swap_comparator = [](std::string_view comparator) -> std::string {
        if (comparator == " < ") {
            return " > ";
        }
        if (comparator == " > ") {
            return " < ";
        }
        if (comparator == " <= ") {
            return " >= ";
        }
        if (comparator == " >= ") {
            return " <= ";
        }
        return std::string(comparator);
    };
    const auto try_parse_condition_header =
        [](std::string_view line,
           std::string* prefix,
           std::string* condition,
           std::string* suffix) {
            if (prefix == nullptr || condition == nullptr || suffix == nullptr) {
                return false;
            }

            const std::string indent =
                std::string(line.substr(0, line.find_first_not_of(" \t")));
            const std::string_view trimmed =
                refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(line));
            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                *prefix = indent + "if ";
                *condition = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
                *suffix = " then";
                return true;
            }
            if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                *prefix = indent + "elseif ";
                *condition = std::string(
                    refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
                *suffix = " then";
                return true;
            }
            return false;
        };
    const auto is_timing_world_frame_call = [](std::string_view value) {
        value = refii::lua::TrimLuaWhitespace(value);
        return value == "Timing.GetWorldFrame()";
    };
    const auto lhs_is_safe_for_timing_swap = [](std::string_view value) {
        value = refii::lua::TrimLuaWhitespace(value);
        if (value.empty() || value.find(',') != std::string_view::npos ||
            value.find(" and ") != std::string_view::npos ||
            value.find(" or ") != std::string_view::npos) {
            return false;
        }
        std::size_t search = 0;
        while ((search = value.find('(', search)) != std::string_view::npos) {
            const std::size_t name_end = search;
            std::size_t name_begin = name_end;
            while (name_begin > 0) {
                const char ch = value[name_begin - 1];
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                      (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == ':')) {
                    break;
                }
                --name_begin;
            }
            const std::string_view callee = value.substr(name_begin, name_end - name_begin);
            if (callee.empty()) {
                ++search;
                continue;
            }
            if (callee != "Timing.GetTickRate") {
                return false;
            }
            ++search;
        }
        return true;
    };

    bool changed = false;
    for (std::string& line : *lines) {
        std::string prefix;
        std::string condition;
        std::string suffix;
        if (!try_parse_condition_header(line, &prefix, &condition, &suffix)) {
            continue;
        }

        std::size_t compare_position = std::string::npos;
        std::string comparator;
        if (!find_top_level_relational(condition, &compare_position, &comparator)) {
            continue;
        }

        const std::string lhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(0, compare_position)));
        const std::string rhs = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition).substr(compare_position + comparator.size())));
        if (!is_timing_world_frame_call(rhs) || !lhs_is_safe_for_timing_swap(lhs)) {
            continue;
        }

        line = prefix + rhs + swap_comparator(comparator) + lhs + suffix;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaNearExactHeadGlobalSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    std::size_t nonempty_head_lines = 0;
    constexpr std::size_t kMaxHeadWindowNonEmptyLines = 64;
    for (std::size_t index = 0; index < lines->size(); ++index) {
        std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment((*lines)[index]));
        if (trimmed.starts_with("function ") || trimmed.starts_with("local function ") ||
            trimmed.find("= function(") != std::string_view::npos) {
            nonempty_head_lines = 0;
        }
        if (trimmed.empty()) {
            continue;
        }
        if (++nonempty_head_lines > kMaxHeadWindowNonEmptyLines) {
            continue;
        }
        if ((*lines)[index].find("--") != std::string::npos) {
            continue;
        }

        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!refii::lua::ParseRenderedAssignmentLine(
                (*lines)[index], &is_local, &lhs_names, &rhs) ||
            lhs_names.size() != 1) {
            continue;
        }
        const std::string_view lhs_trimmed =
            refii::lua::TrimLuaWhitespace(lhs_names.front());
        if (!refii::lua::IsValidLuaIdentifier(lhs_trimmed)) {
            continue;
        }

        std::string global_name;
        if (!TryParseRenderedNearExactGlobalSeedName(rhs, &global_name)) {
            continue;
        }
        if (global_name.empty()) {
            continue;
        }

        const std::size_t indent_end = (*lines)[index].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : (*lines)[index].substr(0, indent_end);
        std::string rewritten = indent;
        if (is_local) {
            rewritten += "local ";
        }
        rewritten += std::string(lhs_trimmed);
        rewritten += " = ";
        rewritten += global_name;
        if (rewritten == (*lines)[index]) {
            continue;
        }
        (*lines)[index] = std::move(rewritten);
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua
