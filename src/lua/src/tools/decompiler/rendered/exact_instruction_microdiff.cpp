#include "tools/decompiler/rendered/exact_instruction_microdiff.h"

#include "tools/decompiler/rendered/exact_seed_normalization.h"
#include "tools/decompiler/rendered/shared_table_cleanup_repairs.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <cstddef>
#include <cstdlib>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua::internal {

namespace {

bool ApplyMicrodiffCallback(bool (*callback)(std::vector<std::string>* lines),
                            std::vector<std::string>* lines) {
    return callback != nullptr && callback(lines);
}

}  // namespace


bool ApplyRenderedLuaSharedInstructionMicroDiffTailWithCallbacks(
    std::vector<std::string>* lines,
    const ExactInstructionMicrodiffCallbacks& callbacks) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    changed |= refii::lua::SimplifyRenderedLuaExplicitSelfMethodArguments(lines);
    changed |= refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments(lines);
    changed |= refii::lua::SimplifyRenderedLuaMethodArg0SelfParameters(lines);
    changed |= ApplyMicrodiffCallback(
        callbacks.try_swap_return_self_right_hand_comparisons, lines);
    changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedCallFieldGuardsForParity(lines);
    changed |= refii::lua::NormalizeRenderedLuaMeterPercentLocalForParity(lines);
    changed |= refii::lua::NormalizeRenderedLuaDebugDrawTextZOffsetForParity(lines);
    return changed;
}

bool NormalizeRenderedLuaCombatZoneForwardLaddersForParity(std::vector<std::string>* lines);

bool NormalizeRenderedLuaStateElseIfChainsToSequentialIfsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 4) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return TrimWhitespace(StripLineComment(line));
    };
    const auto is_state_machine_compare = [](std::string_view lhs,
                                             std::string_view rhs) {
        lhs = TrimWhitespace(lhs);
        rhs = TrimWhitespace(rhs);
        if (lhs.empty() || rhs.empty()) {
            return false;
        }
        const bool numeric_state =
            (lhs.find("CurrentState") != std::string_view::npos ||
             lhs.find(".State") != std::string_view::npos ||
             lhs.find("QuestState") != std::string_view::npos ||
             lhs.find(".QuestState") != std::string_view::npos) &&
            rhs.find_first_not_of("0123456789") == std::string_view::npos;
        if (numeric_state) {
            return true;
        }
        const bool enum_state_lhs =
            lhs.find("QuestStage") != std::string_view::npos ||
            lhs.find(".QuestStage") != std::string_view::npos ||
            lhs.find(".Stage") != std::string_view::npos;
        const bool enum_state_rhs =
            rhs.find(".Stage.") != std::string_view::npos ||
            rhs.find(".State.") != std::string_view::npos;
        return enum_state_lhs && enum_state_rhs;
    };
    const auto parse_state_compare = [&](std::string_view line,
                                         std::string* lhs_out,
                                         std::string* rhs_out) {
        if (lhs_out == nullptr || rhs_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = trim_line(line);
        const bool is_if = trimmed.starts_with("if ") && trimmed.ends_with(" then");
        const bool is_elseif =
            trimmed.starts_with("elseif ") && trimmed.ends_with(" then");
        if (!is_if && !is_elseif) {
            return false;
        }
        const std::size_t prefix_size = is_if ? 3 : 7;
        const std::string_view condition =
            TrimWhitespace(trimmed.substr(prefix_size, trimmed.size() - prefix_size - 5));
        const std::size_t equals = condition.find(" == ");
        if (equals == std::string_view::npos) {
            return false;
        }
        const std::string lhs =
            std::string(TrimWhitespace(condition.substr(0, equals)));
        const std::string rhs =
            std::string(TrimWhitespace(condition.substr(equals + 4)));
        if (!is_state_machine_compare(lhs, rhs)) {
            return false;
        }
        *lhs_out = lhs;
        *rhs_out = rhs;
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 1; line_index + 1 < lines->size(); ++line_index) {
        const std::string_view trimmed = trim_line((*lines)[line_index]);
        if (!trimmed.starts_with("elseif ") || !trimmed.ends_with(" then")) {
            continue;
        }

        std::string current_lhs;
        std::string current_rhs;
        if (!parse_state_compare((*lines)[line_index], &current_lhs, &current_rhs)) {
            continue;
        }

        std::size_t previous_header = line_index;
        while (previous_header > 0) {
            --previous_header;
            const std::string_view previous_trimmed = trim_line((*lines)[previous_header]);
            if (previous_trimmed.empty()) {
                continue;
            }
            if (!previous_trimmed.ends_with(" then")) {
                continue;
            }
            break;
        }
        if (previous_header >= line_index) {
            continue;
        }

        std::string previous_lhs;
        std::string previous_rhs;
        if (!parse_state_compare((*lines)[previous_header], &previous_lhs, &previous_rhs) ||
            previous_lhs != current_lhs) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        if (GetLuaLineIndent((*lines)[previous_header]) != indent) {
            continue;
        }

        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index), indent + "end");
        (*lines)[line_index + 1] = indent + "if " + current_lhs + " == " + current_rhs + " then";
        changed = true;
        ++line_index;
    }

    return changed;
}

bool TryApplyRenderedLuaSeedReturnDebugMicroDiffBundle(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    changed |= refii::lua::ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaInlineLocalTableSeedFieldsForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaReturnThenFallbackElseForParity(lines);
    changed |= refii::lua::NormalizeRenderedLuaDebugDrawTextZOffsetForParity(lines);
    return changed;
}

bool TryApplyRenderedLuaNearExactInstructionMicroDiffCleanupWithCallbacks(
    std::vector<std::string>* lines,
    const ExactInstructionMicrodiffCallbacks& callbacks) {
    if (lines == nullptr) {
        return false;
    }
    bool changed = false;
    changed |= ApplyMicrodiffCallback(
        callbacks.apply_shared_instruction_microdiff_core, lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateSingleUseGuardTemps(lines);
    changed |= refii::lua::SimplifyRenderedLuaNestedAssignedGuardChains(lines);
    changed |= refii::lua::SimplifyRenderedLuaNearbyAssignedRepeatedConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaAdjacentDuplicateCallValueConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateAssignedConditionExpressionUses(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedLocalCallPropertyUsesForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedLocalCallExpressionUsesForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaRepeatedLocalExpressionUsesForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaScopedMemberAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelMemberAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelFunctionAliasAssignments(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelImmediateResultAliases(lines);
    changed |= ApplyMicrodiffCallback(
        callbacks.normalize_inline_singleton_field_table_seeds, lines);
    changed |= ApplyMicrodiffCallback(
        callbacks.normalize_self_alias_table_builder_seeds, lines);
    changed |= refii::lua::ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaInlineLocalTableSeedFieldsForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaReturnThenFallbackElseForParity(lines);
    changed |= refii::lua::ExpandRenderedLuaOrTableFallbackAssignmentsForParity(lines);
    changed |= refii::lua::NormalizeRenderedLuaFreshTargetTableSeeds(lines);
    changed |= refii::lua::NormalizeRenderedLuaStaleTargetTableAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaStaleTargetLiteralOverwrites(lines);
    changed |= refii::lua::SimplifyRenderedLuaTableBuilders(lines);
    changed |= refii::lua::SimplifyRenderedLuaLocalTableBuilderCallArguments(lines);
    changed |= refii::lua::SimplifyRenderedLuaLocalTableLiteralCallAssignments(lines);
    changed |= NormalizeRenderedLuaStateElseIfChainsToSequentialIfsForParity(lines);
    changed |= refii::lua::SimplifyRenderedLuaNestedGuardedDuplicateBodies(lines);
    changed |= refii::lua::SimplifyRenderedLuaNestedGuardedFallbackDuplicateBodies(lines);
    changed |= NormalizeRenderedLuaCombatZoneForwardLaddersForParity(lines);
    changed |= ApplyRenderedLuaSharedInstructionMicroDiffTailWithCallbacks(
        lines, callbacks);
    return changed;
}

bool TryApplyRenderedLuaTinyInstructionMicroDiffCleanupWithCallbacks(
    std::vector<std::string>* lines,
    const ExactInstructionMicrodiffCallbacks& callbacks) {
    if (lines == nullptr) {
        return false;
    }
    bool changed = false;
    changed |= ApplyMicrodiffCallback(
        callbacks.apply_shared_instruction_microdiff_core, lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelImmediateResultAliases(lines);
    changed |= ApplyMicrodiffCallback(
        callbacks.normalize_inline_singleton_field_table_seeds, lines);
    changed |= ApplyMicrodiffCallback(
        callbacks.normalize_self_alias_table_builder_seeds, lines);
    changed |= refii::lua::ExpandRenderedLuaPredicateBooleanReturnsForParity(lines);
    changed |= NormalizeRenderedLuaStateElseIfChainsToSequentialIfsForParity(lines);
    changed |= ApplyRenderedLuaSharedInstructionMicroDiffTailWithCallbacks(
        lines, callbacks);
    return changed;
}

bool NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(std::vector<std::string>* lines);

bool NormalizeRenderedLuaSingleAssertNilPolarityForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct AssertNilCandidate {
        std::size_t line_index = 0;
        std::string indent;
        std::string expression;
        bool was_not_equal = false;
    };

    const auto is_safe_dotted_identifier = [](std::string_view expression) {
        expression = TrimWhitespace(expression);
        if (expression.empty() || expression.front() == '.' || expression.back() == '.') {
            return false;
        }
        bool expect_identifier_start = true;
        bool saw_identifier_char = false;
        for (const char ch : expression) {
            const unsigned char uch = static_cast<unsigned char>(ch);
            if (ch == '.') {
                if (expect_identifier_start || !saw_identifier_char) {
                    return false;
                }
                expect_identifier_start = true;
                saw_identifier_char = false;
                continue;
            }
            if (expect_identifier_start) {
                if (!(std::isalpha(uch) || ch == '_')) {
                    return false;
                }
                expect_identifier_start = false;
                saw_identifier_char = true;
                continue;
            }
            if (!(std::isalnum(uch) || ch == '_')) {
                return false;
            }
            saw_identifier_char = true;
        }
        return !expect_identifier_start && saw_identifier_char;
    };

    const auto parse_assert_nil = [&](std::size_t line_index,
                                      AssertNilCandidate* candidate) {
        if (candidate == nullptr) {
            return false;
        }
        const std::string_view stripped = StripLineComment((*lines)[line_index]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (!StartsWith(trimmed, "assert(") || !trimmed.ends_with(")")) {
            return false;
        }
        std::string_view inner = TrimWhitespace(trimmed.substr(7, trimmed.size() - 8));
        while (inner.size() >= 2 && inner.front() == '(' && inner.back() == ')') {
            inner = TrimWhitespace(inner.substr(1, inner.size() - 2));
        }

        bool was_not_equal = false;
        std::size_t operator_pos = inner.find(" ~= nil");
        if (operator_pos != std::string_view::npos &&
            operator_pos + 7 == inner.size()) {
            was_not_equal = true;
        } else {
            operator_pos = inner.find(" == nil");
            if (operator_pos == std::string_view::npos ||
                operator_pos + 7 != inner.size()) {
                return false;
            }
        }

        const std::string_view expression = TrimWhitespace(inner.substr(0, operator_pos));
        if (!is_safe_dotted_identifier(expression)) {
            return false;
        }

        candidate->line_index = line_index;
        candidate->indent = GetLuaLineIndent((*lines)[line_index]);
        candidate->expression = std::string(expression);
        candidate->was_not_equal = was_not_equal;
        return true;
    };

    std::vector<AssertNilCandidate> candidates;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        AssertNilCandidate candidate;
        if (parse_assert_nil(line_index, &candidate)) {
            candidates.push_back(std::move(candidate));
        }
    }
    if (candidates.size() != 1) {
        return false;
    }

    const AssertNilCandidate& candidate = candidates.front();
    (*lines)[candidate.line_index] =
        candidate.indent + "assert(" + candidate.expression +
        (candidate.was_not_equal ? " == nil)" : " ~= nil)");
    return true;
}

bool NormalizeRenderedLuaReturnedLocalTableSelfUsesForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_local_table_seed = [](std::string_view line,
                                           std::string* local_out) {
        if (local_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (!StartsWith(trimmed, "local ")) {
            return false;
        }
        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos) {
            return false;
        }
        const std::string name =
            std::string(TrimWhitespace(trimmed.substr(6, equals - 6)));
        const std::string_view rhs = TrimWhitespace(trimmed.substr(equals + 1));
        if (!refii::lua::IsValidLuaIdentifier(name) || rhs != "{") {
            return false;
        }
        *local_out = name;
        return true;
    };

    bool changed = false;
    for (std::size_t seed_line = 0; seed_line < lines->size(); ++seed_line) {
        std::string local_name;
        if (!parse_local_table_seed((*lines)[seed_line], &local_name)) {
            continue;
        }

        const std::string body_indent = GetLuaLineIndent((*lines)[seed_line]);
        std::size_t return_line = lines->size();
        for (std::size_t line_index = seed_line + 1; line_index < lines->size(); ++line_index) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment((*lines)[line_index]));
            if (trimmed == "return " + local_name) {
                return_line = line_index;
                break;
            }
            if (trimmed == "end" &&
                GetLuaLineIndent((*lines)[line_index]).size() < body_indent.size()) {
                break;
            }
        }
        if (return_line == lines->size()) {
            continue;
        }

        std::string alias_name;
        std::size_t setmetatable_line = lines->size();
        std::size_t method_line = lines->size();
        for (std::size_t line_index = seed_line + 1; line_index < return_line; ++line_index) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment((*lines)[line_index]));
            if (StartsWith(trimmed, "setmetatable(")) {
                const std::size_t comma = trimmed.find(',');
                if (comma != std::string_view::npos) {
                    const std::string candidate_alias = std::string(
                        TrimWhitespace(trimmed.substr(13, comma - 13)));
                    if (candidate_alias != local_name &&
                        refii::lua::IsValidLuaIdentifier(candidate_alias) &&
                        trimmed.find(", self)") != std::string_view::npos) {
                        alias_name = candidate_alias;
                        setmetatable_line = line_index;
                    }
                }
            }
        }
        if (alias_name.empty()) {
            continue;
        }

        const std::string method_prefix = alias_name + ":";
        for (std::size_t line_index = setmetatable_line + 1; line_index < return_line;
             ++line_index) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment((*lines)[line_index]));
            if (StartsWith(trimmed, method_prefix)) {
                method_line = line_index;
                break;
            }
        }
        if (method_line == lines->size()) {
            continue;
        }

        const std::string setmetatable_indent = GetLuaLineIndent((*lines)[setmetatable_line]);
        (*lines)[setmetatable_line] =
            setmetatable_indent + "setmetatable(" + local_name + ", self)";

        std::string method_text = (*lines)[method_line];
        const std::size_t alias_pos = method_text.find(alias_name + ":");
        if (alias_pos == std::string::npos) {
            continue;
        }
        method_text.replace(alias_pos, alias_name.size(), local_name);
        (*lines)[method_line] = std::move(method_text);
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaFallbackTempAssignmentUsesForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto strip_outer_parens = [](std::string_view expression) {
        expression = TrimWhitespace(expression);
        while (expression.size() >= 2 && expression.front() == '(' &&
               expression.back() == ')') {
            expression = TrimWhitespace(expression.substr(1, expression.size() - 2));
        }
        return expression;
    };

    const auto parse_simple_assignment = [](std::string_view line,
                                            std::string* lhs_out,
                                            std::string* rhs_out) {
        if (lhs_out == nullptr || rhs_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (StartsWith(trimmed, "local ") || StartsWith(trimmed, "if ") ||
            trimmed == "end") {
            return false;
        }
        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos ||
            (equals > 0 && trimmed[equals - 1] == '~') ||
            (equals + 1 < trimmed.size() && trimmed[equals + 1] == '=')) {
            return false;
        }
        *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, equals)));
        *rhs_out = std::string(TrimWhitespace(trimmed.substr(equals + 1)));
        return !lhs_out->empty() && !rhs_out->empty();
    };

    const auto replace_assignment_rhs = [&](std::size_t line_index,
                                            std::string_view temp_name) {
        std::string lhs;
        std::string rhs;
        if (!parse_simple_assignment((*lines)[line_index], &lhs, &rhs)) {
            return false;
        }
        (*lines)[line_index] =
            GetLuaLineIndent((*lines)[line_index]) + lhs + " = " +
            std::string(temp_name);
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment((*lines)[line_index]));

        std::string direct_lhs;
        std::string direct_rhs;
        if (parse_simple_assignment((*lines)[line_index], &direct_lhs, &direct_rhs)) {
            const std::string_view direct_rhs_stripped = strip_outer_parens(direct_rhs);
            const std::size_t or_pos = direct_rhs_stripped.find(" or ");
            if (or_pos != std::string_view::npos && line_index + 3 < lines->size()) {
                std::string temp_name;
                std::string temp_rhs;
                if (StartsWith(TrimWhitespace(StripLineComment((*lines)[line_index + 1])),
                               "if not ") &&
                    parse_simple_assignment((*lines)[line_index + 2],
                                            &temp_name,
                                            &temp_rhs) &&
                    refii::lua::IsValidLuaIdentifier(temp_name) &&
                    TrimWhitespace(StripLineComment((*lines)[line_index + 3])) == "end") {
                    const std::string indent = GetLuaLineIndent((*lines)[line_index]);
                    (*lines)[line_index] =
                        indent + "local " + temp_name + " = " +
                        std::string(direct_rhs_stripped);
                    lines->insert(lines->begin() +
                                      static_cast<std::ptrdiff_t>(line_index + 1),
                                  indent + direct_lhs + " = " + temp_name);
                    changed = true;
                    ++line_index;
                    continue;
                }
            }
        }

        if (StartsWith(trimmed, "local ")) {
            const std::size_t equals = trimmed.find('=');
            if (equals != std::string_view::npos) {
                const std::string temp_name =
                    std::string(TrimWhitespace(trimmed.substr(6, equals - 6)));
                std::string_view rhs = strip_outer_parens(trimmed.substr(equals + 1));
                const std::size_t or_pos = rhs.find(" or ");
                if (refii::lua::IsValidLuaIdentifier(temp_name) &&
                    or_pos != std::string_view::npos) {
                    const std::string expr =
                        std::string(strip_outer_parens(rhs.substr(0, or_pos)));
                    if (!expr.empty() && line_index + 1 < lines->size()) {
                        std::string lhs;
                        std::string assignment_rhs;
                        if (parse_simple_assignment((*lines)[line_index + 1],
                                                    &lhs,
                                                    &assignment_rhs) &&
                            assignment_rhs == expr &&
                            replace_assignment_rhs(line_index + 1, temp_name)) {
                            changed = true;
                        }
                    }
                }
            }
            continue;
        }

        if (!StartsWith(trimmed, "if not ") || !trimmed.ends_with(" then")) {
            continue;
        }
        const std::string expr = std::string(
            strip_outer_parens(trimmed.substr(7, trimmed.size() - 12)));
        if (expr.empty() || line_index + 3 >= lines->size()) {
            continue;
        }

        std::string temp_lhs;
        std::string temp_rhs;
        if (!parse_simple_assignment((*lines)[line_index + 1], &temp_lhs, &temp_rhs) ||
            !refii::lua::IsValidLuaIdentifier(temp_lhs) ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        std::string lhs;
        std::string assignment_rhs;
        if (parse_simple_assignment((*lines)[line_index + 3], &lhs, &assignment_rhs) &&
            assignment_rhs == expr &&
            replace_assignment_rhs(line_index + 3, temp_lhs)) {
            changed = true;
        }
    }

    return changed;
}

bool NormalizeRenderedLuaBooleanCallArgumentLocalsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_bool_assignment = [](std::string_view line,
                                          std::string* name_out,
                                          std::string* value_out) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs;
        if (!refii::lua::ParseRenderedAssignmentLine(line, &is_local, &lhs_names, &rhs) ||
            is_local || lhs_names.size() != 1 || (rhs != "true" && rhs != "false") ||
            !refii::lua::IsValidLuaIdentifier(lhs_names.front())) {
            return false;
        }
        if (name_out != nullptr) {
            *name_out = lhs_names.front();
        }
        if (value_out != nullptr) {
            *value_out = rhs;
        }
        return true;
    };

    const auto later_call_uses = [&](std::size_t start,
                                     std::string_view name,
                                     std::size_t span) {
        const std::string needle1 = ", " + std::string(name) + ",";
        const std::string needle2 = ", " + std::string(name) + ")";
        for (std::size_t probe = start; probe < std::min(lines->size(), start + span);
             ++probe) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment((*lines)[probe]));
            if (trimmed.find('(') != std::string_view::npos &&
                (trimmed.find(needle1) != std::string_view::npos ||
                 trimmed.find(needle2) != std::string_view::npos)) {
                return true;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        std::string first_name;
        std::string first_value;
        std::string second_name;
        std::string second_value;
        if (!parse_bool_assignment((*lines)[line_index], &first_name, &first_value) ||
            !parse_bool_assignment((*lines)[line_index + 1], &second_name, &second_value) ||
            first_name == second_name || first_value != "true" || second_value != "true" ||
            !later_call_uses(line_index + 2, first_name, 64) ||
            !later_call_uses(line_index + 2, second_name, 64)) {
            continue;
        }
        (*lines)[line_index].insert((*lines)[line_index].find_first_not_of(" \t"), "local ");
        (*lines)[line_index + 1].insert(
            (*lines)[line_index + 1].find_first_not_of(" \t"), "local ");
        changed = true;
        ++line_index;
    }

    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string_view if_trimmed = TrimWhitespace(StripLineComment((*lines)[line_index]));
        std::string name;
        std::string inner_value;
        std::string after_name;
        std::string after_value;
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then") ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) != "end" ||
            !parse_bool_assignment((*lines)[line_index + 1], &name, &inner_value) ||
            !parse_bool_assignment((*lines)[line_index + 3], &after_name, &after_value) ||
            name != after_name || inner_value != "false" || after_value != "true" ||
            !later_call_uses(line_index + 4, name, 12)) {
            continue;
        }
        std::string condition =
            std::string(TrimWhitespace(if_trimmed.substr(3, if_trimmed.size() - 8)));
        const std::size_t not_equal_pos = condition.find(" ~= ");
        if (not_equal_pos == std::string::npos) {
            continue;
        }
        condition.replace(not_equal_pos, 4, " == ");
        const std::string replacement =
            GetLuaLineIndent((*lines)[line_index]) + "local " + name + " = " + condition;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index), replacement);
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaEmptyLimitGuardCallDefaultForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment((*lines)[line_index]));
        if (!StartsWith(if_trimmed, "if not ") || !if_trimmed.ends_with(" then") ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) != "end") {
            continue;
        }

        const std::string guarded_expression = std::string(
            TrimWhitespace(if_trimmed.substr(7, if_trimmed.size() - 12)));
        if (!guarded_expression.ends_with(".Limit")) {
            continue;
        }

        std::string call_line = (*lines)[line_index + 2];
        const std::string_view call_trimmed = TrimWhitespace(StripLineComment(call_line));
        if (!StartsWith(call_trimmed, "Health.SetRegeneration(") ||
            call_trimmed.find(guarded_expression) == std::string_view::npos ||
            call_trimmed.find(guarded_expression + " or ") != std::string_view::npos) {
            continue;
        }

        const std::size_t expression_pos = call_line.find(guarded_expression);
        if (expression_pos == std::string::npos) {
            continue;
        }
        call_line.replace(expression_pos,
                          guarded_expression.size(),
                          guarded_expression + " or 100");
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2));
        (*lines)[line_index] = std::move(call_line);
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaIndexedStateTableBuildersForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto strip_outer_parens = [](std::string_view expression) {
        expression = TrimWhitespace(expression);
        while (expression.size() >= 2 && expression.front() == '(' &&
               expression.back() == ')') {
            expression = TrimWhitespace(expression.substr(1, expression.size() - 2));
        }
        return expression;
    };

    const auto parse_simple_assignment = [&](std::string_view line,
                                             std::string* lhs_out,
                                             std::string* rhs_out) {
        if (lhs_out == nullptr || rhs_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (StartsWith(trimmed, "local ") || StartsWith(trimmed, "if ") ||
            StartsWith(trimmed, "for ") || StartsWith(trimmed, "function ") ||
            trimmed == "else" || trimmed == "end") {
            return false;
        }
        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos ||
            (equals > 0 && (trimmed[equals - 1] == '~' ||
                            trimmed[equals - 1] == '<' ||
                            trimmed[equals - 1] == '>')) ||
            (equals + 1 < trimmed.size() && trimmed[equals + 1] == '=')) {
            return false;
        }
        *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, equals)));
        *rhs_out = std::string(strip_outer_parens(trimmed.substr(equals + 1)));
        return !lhs_out->empty() && !rhs_out->empty();
    };

    const auto parse_local_empty_table = [&](std::string_view line,
                                             std::string* local_out) {
        if (local_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (!StartsWith(trimmed, "local ")) {
            return false;
        }
        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos) {
            return false;
        }
        const std::string name =
            std::string(TrimWhitespace(trimmed.substr(6, equals - 6)));
        const std::string_view rhs =
            strip_outer_parens(trimmed.substr(equals + 1));
        if (!refii::lua::IsValidLuaIdentifier(name) || rhs != "{}") {
            return false;
        }
        *local_out = name;
        return true;
    };

    const auto parent_from_index_entry = [](std::string_view entry,
                                            std::string* parent_out) {
        if (parent_out == nullptr || !entry.ends_with("[i]")) {
            return false;
        }
        const std::string parent = std::string(entry.substr(0, entry.size() - 3));
        if (parent.empty() || parent.find('=') != std::string::npos ||
            parent.find('{') != std::string::npos ||
            parent.find('}') != std::string::npos) {
            return false;
        }
        *parent_out = parent;
        return true;
    };

    const auto parse_if_index_lookup = [](std::string_view line,
                                          std::string* lookup_out,
                                          std::string* key_out) {
        if (lookup_out == nullptr || key_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (!StartsWith(trimmed, "if ") || !trimmed.ends_with(" then")) {
            return false;
        }
        const std::string lookup =
            std::string(TrimWhitespace(trimmed.substr(3, trimmed.size() - 8)));
        const std::size_t open = lookup.find('[');
        const std::size_t close = lookup.rfind(']');
        if (open == std::string::npos || close != lookup.size() - 1 ||
            open == 0 || close <= open + 1) {
            return false;
        }
        const std::string key =
            std::string(TrimWhitespace(std::string_view(lookup).substr(open + 1,
                                                                        close - open - 1)));
        if (!refii::lua::IsValidLuaIdentifier(key)) {
            return false;
        }
        *lookup_out = lookup;
        *key_out = key;
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 16 < lines->size(); ++line_index) {
        std::string lookup_expression;
        std::string key_name;
        if (!parse_if_index_lookup((*lines)[line_index], &lookup_expression, &key_name)) {
            continue;
        }

        std::string temp_name;
        if (!parse_local_empty_table((*lines)[line_index + 1], &temp_name)) {
            continue;
        }

        std::string name_lhs;
        std::string name_rhs;
        std::string func_lhs;
        std::string func_rhs;
        std::string parent_lhs;
        std::string parent_rhs;
        std::string entry_lhs;
        std::string entry_rhs;
        std::string entry_expression;
        std::string parent_expression;
        if (!parse_simple_assignment((*lines)[line_index + 2], &name_lhs, &name_rhs) ||
            !parse_simple_assignment((*lines)[line_index + 3], &func_lhs, &func_rhs) ||
            !parse_simple_assignment((*lines)[line_index + 4], &parent_lhs, &parent_rhs) ||
            !parse_simple_assignment((*lines)[line_index + 5], &entry_lhs, &entry_rhs) ||
            !name_lhs.ends_with(".Name") || !func_lhs.ends_with(".Func") ||
            name_lhs.substr(0, name_lhs.size() - 5) !=
                func_lhs.substr(0, func_lhs.size() - 5) ||
            name_rhs != key_name || func_rhs != lookup_expression ||
            parent_rhs != "{}" || entry_rhs != temp_name ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 6])) != "else") {
            continue;
        }
        entry_expression = name_lhs.substr(0, name_lhs.size() - 5);
        if (entry_lhs != entry_expression ||
            !parent_from_index_entry(entry_expression, &parent_expression) ||
            parent_lhs != parent_expression) {
            continue;
        }

        const std::string_view error_local_line =
            TrimWhitespace(StripLineComment((*lines)[line_index + 7]));
        if (!StartsWith(error_local_line, "local ")) {
            continue;
        }
        const std::size_t error_equals = error_local_line.find('=');
        if (error_equals == std::string_view::npos) {
            continue;
        }
        const std::string error_local =
            std::string(TrimWhitespace(error_local_line.substr(6, error_equals - 6)));
        const std::string error_message =
            std::string(strip_outer_parens(error_local_line.substr(error_equals + 1)));
        if (!refii::lua::IsValidLuaIdentifier(error_local) ||
            error_message.find(key_name) == std::string::npos ||
            !StartsWith(TrimWhitespace(StripLineComment((*lines)[line_index + 8])),
                        "Debug.Error(")) {
            continue;
        }

        std::string reset_lhs;
        std::string reset_rhs;
        std::string missing_name_lhs;
        std::string missing_name_rhs;
        std::string reseed_lhs;
        std::string reseed_rhs;
        std::string missing_entry_lhs;
        std::string missing_entry_rhs;
        if (!parse_simple_assignment((*lines)[line_index + 9], &reset_lhs, &reset_rhs) ||
            reset_lhs != temp_name || reset_rhs != "{}" ||
            !parse_simple_assignment((*lines)[line_index + 10],
                                     &missing_name_lhs,
                                     &missing_name_rhs) ||
            missing_name_lhs != temp_name + ".Name" ||
            missing_name_rhs.find(key_name) == std::string::npos ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 11])) !=
                "function " + temp_name + ".Func()" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 12])) != "return false" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 13])) != "end" ||
            !parse_simple_assignment((*lines)[line_index + 14], &reseed_lhs, &reseed_rhs) ||
            reseed_lhs != parent_expression || reseed_rhs != "{}" ||
            !parse_simple_assignment((*lines)[line_index + 15],
                                     &missing_entry_lhs,
                                     &missing_entry_rhs) ||
            missing_entry_lhs != entry_expression || missing_entry_rhs != temp_name ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 16])) != "end") {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index + 1]);
        std::vector<std::string> replacement = {
            indent + entry_expression + " = {",
            indent + "  Name = " + key_name + ",",
            indent + "  Func = " + lookup_expression + ",",
            indent + "}",
            indent + "else",
            indent + "Debug.Error(" + error_message + ")",
            indent + entry_expression + " = {",
            indent + "  Name = " + missing_name_rhs + ",",
            indent + "  Func = function()",
            indent + "    return false",
            indent + "  end,",
            indent + "}",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 16));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        line_index += replacement.size();
    }

    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        std::string temp_name;
        if (!parse_local_empty_table((*lines)[line_index], &temp_name)) {
            continue;
        }

        std::string name_lhs;
        std::string name_rhs;
        std::string func_lhs;
        std::string func_rhs;
        if (!parse_simple_assignment((*lines)[line_index + 1], &name_lhs, &name_rhs) ||
            !parse_simple_assignment((*lines)[line_index + 2], &func_lhs, &func_rhs) ||
            name_lhs != temp_name + ".Name" || func_lhs != temp_name + ".Func") {
            continue;
        }

        const std::string_view insert_trimmed =
            TrimWhitespace(StripLineComment((*lines)[line_index + 3]));
        constexpr std::string_view prefix = "table.insert(";
        constexpr std::string_view suffix = ", {})";
        if (!StartsWith(insert_trimmed, prefix) || !insert_trimmed.ends_with(suffix)) {
            continue;
        }
        const std::string insert_target =
            std::string(TrimWhitespace(insert_trimmed.substr(prefix.size(),
                                                             insert_trimmed.size() -
                                                                 prefix.size() -
                                                                 suffix.size())));
        if (insert_target.empty()) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        std::vector<std::string> replacement = {
            indent + "table.insert(" + insert_target + ", {",
            indent + "  Name = " + name_rhs + ",",
            indent + "  Func = " + func_rhs + ",",
            indent + "})",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        if (line_index + 8 < lines->size() &&
            TrimWhitespace(StripLineComment((*lines)[line_index + 7])) == "else") {
            const std::string_view following_if =
                TrimWhitespace(StripLineComment((*lines)[line_index + 8]));
            if (following_if == "if cos_theta < math.cos(math.rad(135)) then") {
                (*lines)[line_index + 7] =
                    indent + "elseif cos_theta < math.cos(math.rad(135)) then";
                lines->erase(lines->begin() +
                             static_cast<std::ptrdiff_t>(line_index + 8));

                std::size_t next_function = lines->size();
                for (std::size_t probe = line_index + 8; probe < lines->size(); ++probe) {
                    if (StartsWith(TrimWhitespace(StripLineComment((*lines)[probe])),
                                   "function ")) {
                        next_function = probe;
                        break;
                    }
                }
                if (next_function != lines->size()) {
                    for (std::size_t probe = next_function; probe-- > line_index + 8;) {
                        if (TrimWhitespace(StripLineComment((*lines)[probe])) == "end" &&
                            GetLuaLineIndent((*lines)[probe]) == indent) {
                            lines->erase(lines->begin() +
                                         static_cast<std::ptrdiff_t>(probe));
                            break;
                        }
                    }
                }
            }
        }
        changed = true;
        line_index += replacement.size();
    }

    return changed;
}

bool NormalizeRenderedLuaInvalidStateDebugMessageForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_safe_field_expression = [](std::string_view expression) {
        expression = TrimWhitespace(expression);
        if (expression.empty() || expression.front() == '.' || expression.back() == '.') {
            return false;
        }
        for (const char ch : expression) {
            const unsigned char uch = static_cast<unsigned char>(ch);
            if (!(std::isalnum(uch) || ch == '_' || ch == '.')) {
                return false;
            }
        }
        return expression.find("..") == std::string_view::npos;
    };

    const auto parse_local_tostring = [&](std::string_view line,
                                          std::string* name_out,
                                          std::string* expression_out) {
        if (name_out == nullptr || expression_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (!StartsWith(trimmed, "local ")) {
            return false;
        }
        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos) {
            return false;
        }
        const std::string name =
            std::string(TrimWhitespace(trimmed.substr(6, equals - 6)));
        std::string_view rhs = TrimWhitespace(trimmed.substr(equals + 1));
        if (!refii::lua::IsValidLuaIdentifier(name) || !StartsWith(rhs, "tostring(") ||
            !rhs.ends_with(")")) {
            return false;
        }
        rhs = TrimWhitespace(rhs.substr(9, rhs.size() - 10));
        if (!is_safe_field_expression(rhs)) {
            return false;
        }
        *name_out = name;
        *expression_out = std::string(rhs);
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size();) {
        std::string local_name;
        std::string expression;
        if (parse_local_tostring((*lines)[line_index], &local_name, &expression)) {
            const std::string_view next_trimmed =
                TrimWhitespace(StripLineComment((*lines)[line_index + 1]));
            const std::string repeated_call = "tostring(" + expression + ")";
            if (next_trimmed.find(repeated_call) != std::string_view::npos &&
                next_trimmed.find(local_name) == std::string_view::npos) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
                changed = true;
                continue;
            }
        }
        ++line_index;
    }

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment((*lines)[line_index]));
        constexpr std::string_view timing_call = "Timing.GetSecondsSince(";
        const std::size_t less_pos = trimmed.find(" < ");
        const std::size_t timing_pos = trimmed.find(timing_call);
        if (!StartsWith(trimmed, "if self.") || !trimmed.ends_with(" then") ||
            less_pos == std::string_view::npos || timing_pos == std::string_view::npos ||
            timing_pos <= less_pos) {
            continue;
        }
        const std::string left =
            std::string(TrimWhitespace(trimmed.substr(3, less_pos - 3)));
        const std::string right =
            std::string(TrimWhitespace(trimmed.substr(less_pos + 3,
                                                      trimmed.size() - less_pos - 8)));
        if (!is_safe_field_expression(left) || !StartsWith(right, timing_call) ||
            right.find("self.") == std::string::npos) {
            continue;
        }
        (*lines)[line_index] =
            GetLuaLineIndent((*lines)[line_index]) + "if " + right + " > " + left +
            " then";
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaDuplicatedMethodResultChainsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        const std::size_t call_close = trimmed.find("()");
        if (call_close == std::string_view::npos) {
            continue;
        }

        const std::size_t method_colon = trimmed.rfind(':', call_close);
        if (method_colon == std::string_view::npos || method_colon == 0) {
            continue;
        }
        const std::string method =
            std::string(trimmed.substr(method_colon + 1, call_close - method_colon - 1));
        if (!refii::lua::IsValidLuaIdentifier(method)) {
            continue;
        }

        const std::size_t field_dot = trimmed.rfind('.', method_colon);
        if (field_dot == std::string_view::npos || field_dot + 1 >= method_colon) {
            continue;
        }
        const std::string field =
            std::string(trimmed.substr(field_dot + 1, method_colon - field_dot - 1));
        if (!refii::lua::IsValidLuaIdentifier(field)) {
            continue;
        }

        const std::string duplicated_suffix = "()." + field + ":" + method + "()";
        const std::size_t suffix_pos = line.find(duplicated_suffix);
        if (suffix_pos == std::string::npos) {
            continue;
        }
        line.erase(suffix_pos + 2, duplicated_suffix.size() - 2);
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaGuardedLocalOrSeedsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_local_assignment = [](std::string_view line,
                                           std::string* name_out,
                                           std::string* rhs_out) {
        if (name_out == nullptr || rhs_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (!StartsWith(trimmed, "local ")) {
            return false;
        }
        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos) {
            return false;
        }
        const std::string name =
            std::string(TrimWhitespace(trimmed.substr(6, equals - 6)));
        const std::string rhs =
            std::string(TrimWhitespace(trimmed.substr(equals + 1)));
        if (!refii::lua::IsValidLuaIdentifier(name) || rhs.empty() ||
            rhs.find(name) != std::string::npos) {
            return false;
        }
        *name_out = name;
        *rhs_out = rhs;
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment((*lines)[line_index]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then") ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        const std::string guard =
            std::string(TrimWhitespace(if_trimmed.substr(3, if_trimmed.size() - 8)));
        if (!refii::lua::IsValidLuaIdentifier(guard)) {
            continue;
        }

        std::string local_name;
        std::string rhs;
        if (!parse_local_assignment((*lines)[line_index + 1], &local_name, &rhs) ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 3])) !=
                "if " + local_name + " then") {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] = indent + "local " + local_name + " = " + guard + " or " + rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaImmediateIndexedWriteReseedsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_assignment = [](std::string_view line,
                                     std::string* lhs_out,
                                     std::string* rhs_out) {
        if (lhs_out == nullptr || rhs_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (StartsWith(trimmed, "local ") || StartsWith(trimmed, "if ") ||
            StartsWith(trimmed, "for ") || trimmed == "end") {
            return false;
        }
        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos ||
            (equals > 0 && (trimmed[equals - 1] == '~' ||
                            trimmed[equals - 1] == '<' ||
                            trimmed[equals - 1] == '>')) ||
            (equals + 1 < trimmed.size() && trimmed[equals + 1] == '=')) {
            return false;
        }
        *lhs_out = std::string(TrimWhitespace(trimmed.substr(0, equals)));
        *rhs_out = std::string(TrimWhitespace(trimmed.substr(equals + 1)));
        return !lhs_out->empty() && !rhs_out->empty();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size();) {
        std::string table_lhs;
        std::string table_rhs;
        std::string indexed_lhs;
        std::string indexed_rhs;
        if (parse_assignment((*lines)[line_index], &table_lhs, &table_rhs) &&
            table_rhs == "{}" &&
            table_lhs.find('.') != std::string::npos &&
            table_lhs.find('[') == std::string::npos &&
            parse_assignment((*lines)[line_index + 1], &indexed_lhs, &indexed_rhs) &&
            StartsWith(indexed_lhs, table_lhs + "[") &&
            indexed_lhs.find(']', table_lhs.size()) != std::string::npos) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            continue;
        }
        ++line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaCombatUpdateImplicitLocalsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto localize_exact_assignment = [&](std::string_view expected) {
        for (std::string& line : *lines) {
            if (TrimWhitespace(StripLineComment(line)) != expected) {
                continue;
            }
            const std::size_t indent = line.find_first_not_of(" \t");
            if (indent == std::string::npos) {
                continue;
            }
            line.insert(indent, "local ");
            return true;
        }
        return false;
    };

    bool changed = false;
    changed |= localize_exact_assignment("enrage_time = 30");
    changed |= localize_exact_assignment("zonal = false");
    changed |= localize_exact_assignment("is_on_prong = true");
    changed |= localize_exact_assignment(
        "target_vel = (Physics.GetVelocity(self.Target) / Timing.GetTickRate())");
    changed |= localize_exact_assignment("target_speed = target_vel:GetLength()");
    return changed;
}

bool NormalizeRenderedLuaDebugDataSingletonLiteralPatchesForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        constexpr std::string_view prefix = "self.DebugData = {";
        if (!StartsWith(trimmed, prefix) || !trimmed.ends_with("}")) {
            continue;
        }
        const std::string_view inner =
            TrimWhitespace(trimmed.substr(prefix.size(), trimmed.size() - prefix.size() - 1));
        const std::size_t equals = inner.find('=');
        if (equals == std::string_view::npos ||
            inner.find(',', equals) != std::string_view::npos) {
            continue;
        }
        const std::string field =
            std::string(TrimWhitespace(inner.substr(0, equals)));
        const std::string value =
            std::string(TrimWhitespace(inner.substr(equals + 1)));
        if (!refii::lua::IsValidLuaIdentifier(field) || value.empty()) {
            continue;
        }
        line = GetLuaLineIndent(line) + "self.DebugData." + field + " = " + value;
        changed = true;
    }

    changed |= NormalizeRenderedLuaCombatZoneForwardLaddersForParity(lines);
    return changed;
}

bool NormalizeRenderedLuaCombatUpdatePositionLocalsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto localize_exact_assignment = [&](std::string_view expected) {
        for (std::string& line : *lines) {
            if (TrimWhitespace(StripLineComment(line)) != expected) {
                continue;
            }
            const std::size_t indent = line.find_first_not_of(" \t");
            if (indent == std::string::npos ||
                line.compare(indent, 6, "local ") == 0) {
                continue;
            }
            line.insert(indent, "local ");
            return true;
        }
        return false;
    };

    bool changed = false;
    changed |= localize_exact_assignment("in_position = false");
    changed |= localize_exact_assignment("min_angle_tolerance = 0");
    changed |= localize_exact_assignment("strafe_dist = self.EnterStrafeDist");
    return changed;
}

bool NormalizeRenderedLuaCombatUpdateZoneWeaponLaddersForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) !=
                "if self.Style.PreferRanged then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) !=
                "self.DesiredWeaponType = WeaponTypes.Ranged" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 3])) !=
                "self.DesiredWeaponType = WeaponTypes.Melee") {
            continue;
        }

        (*lines)[line_index + 2] = GetLuaLineIndent((*lines)[line_index + 2]) + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4),
                      GetLuaLineIndent((*lines)[line_index]) + "end");
        changed = true;
        break;
    }

    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) !=
                "if self.Style.OuterRingPercentAsRanged then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) !=
                "self.DesiredWeaponType = self.OuterRingWeapon" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 3])) !=
                "if self:HasWeaponOfType(WeaponTypes.Ranged) then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 4])) !=
                "self.DesiredWeaponType = WeaponTypes.Ranged" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 5])) != "end") {
            continue;
        }

        (*lines)[line_index + 2] =
            GetLuaLineIndent((*lines)[line_index + 2]) +
            "elseif self:HasWeaponOfType(WeaponTypes.Ranged) then";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
        break;
    }

    for (std::string& line : *lines) {
        if (TrimWhitespace(StripLineComment(line)) !=
            "dir_to_target = (line_to_attacker * (-1))") {
            continue;
        }
        const std::size_t indent = line.find_first_not_of(" \t");
        if (indent != std::string::npos &&
            line.compare(indent, 6, "local ") != 0) {
            line.insert(indent, "local ");
            changed = true;
        }
        break;
    }

    return changed;
}

bool NormalizeRenderedLuaHealthBarScriptBuildersForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size();) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) ==
                "self.HealthBarScript = {}" &&
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) ==
                "self.HealthBarScript = {}" &&
            StartsWith(TrimWhitespace(StripLineComment((*lines)[line_index + 2])),
                       "self.HealthBarScript.")) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
            changed = true;
            continue;
        }
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) ==
                "self.HealthBarScript.Entity = self.Combatant" &&
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) ==
                "self.HealthBarScript = {}" &&
            StartsWith(TrimWhitespace(StripLineComment((*lines)[line_index + 2])),
                       "function self.HealthBarScript:Update()")) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
            changed = true;
            continue;
        }
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) ==
                "if self.HealthBarScript then" &&
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) ==
                "self.HealthBarScript = {}" &&
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) ==
                "self.HealthBarScript.KillMe = true") {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
            changed = true;
            continue;
        }
        ++line_index;
    }

    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) !=
                "if (Creature.GetCreatureType(self.Combatant) ~= ECreatureType.CREATURE_TROLL) then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) !=
                "is_troll = false" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 3])) !=
                "is_troll = true") {
            continue;
        }
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] =
            indent +
            "local is_troll = Creature.GetCreatureType(self.Entity) == ECreatureType.CREATURE_TROLL";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
        break;
    }

    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        if (!StartsWith(TrimWhitespace(StripLineComment((*lines)[line_index])),
                        "local r") ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) !=
                "if GetPlayerHenchman():IsAlive() then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) !=
                "distance_hench = GetSquaredDistanceBetweenEntities(henchman, self.Entity)" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 3])) !=
                "if (distance_hench < distance) then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 4])) !=
                "distance = distance_hench") {
            continue;
        }
        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        (*lines)[line_index] = indent + "if henchman:IsAlive() then";
        (*lines)[line_index + 1] =
            indent + "local distance_hench = GetSquaredDistanceBetweenEntities(henchman, self.Entity)";
        (*lines)[line_index + 2] = indent + "if distance_hench < distance then";
        changed = true;
        break;
    }

    for (std::size_t line_index = 0; line_index < lines->size();) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) ==
            "r7 = (chase_radius + (4))") {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            continue;
        }
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) ==
            "health_as_percent = 0") {
            (*lines)[line_index] =
                GetLuaLineIndent((*lines)[line_index]) + "local health_as_percent = 0";
            changed = true;
        }
        ++line_index;
    }

    return changed;
}

bool NormalizeRenderedLuaHollowManSpawnOffsetForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view first =
            TrimWhitespace(StripLineComment((*lines)[line_index]));
        const std::string_view second =
            TrimWhitespace(StripLineComment((*lines)[line_index + 1]));
        const std::string_view third =
            TrimWhitespace(StripLineComment((*lines)[line_index + 2]));
        if (first !=
                "offset = (Physics.GetVelocity(target_to_use) * params.TargetSpeedToDistMultiplier)" ||
            second != "offset = (offset + delta)" ||
            !StartsWith(third, "return Creature.GetHollowManMoveDest(targ_pos, offset,")) {
            continue;
        }
        (*lines)[line_index] =
            GetLuaLineIndent((*lines)[line_index]) +
            "local offset = ((Physics.GetVelocity(target_to_use) * params.TargetSpeedToDistMultiplier) + delta)";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaCombatInfoApplyStyleDefaultLocalsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 21 < lines->size(); ++line_index) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) !=
                "local r2 = self.Style.ProngTolerance or (30)" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) !=
                "self.ProngTolerance = r2" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) !=
                "if not self.Style.MovementTolerance then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 3])) != "r2 = 1" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 4])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 5])) !=
                "self.MovementTolerance = r2" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 6])) !=
                "if not self.Style.TurnToFaceTolerance then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 7])) != "r2 = 15" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 8])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 9])) !=
                "self.TurnToFaceTolerance = r2" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 10])) !=
                "if not self.Style.TurnToFaceToleranceIdle then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 11])) !=
                "r2 = (self.TurnToFaceTolerance / (2))" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 12])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 13])) !=
                "self.TurnToFaceToleranceIdle = r2" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 14])) !=
                "if not self.Style.EnterStrafeDist then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 15])) != "r2 = 5" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 16])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 17])) !=
                "self.EnterStrafeDist = r2" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 18])) !=
                "if not self.Style.LeaveStrafeDelta then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 19])) != "r2 = 1" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 20])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 21])) !=
                "self.LeaveStrafeDelta = r2") {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        std::vector<std::string> replacement = {
            indent + "self.ProngTolerance = self.Style.ProngTolerance or (30)",
            indent + "self.MovementTolerance = self.Style.MovementTolerance or 1",
            indent + "self.TurnToFaceTolerance = self.Style.TurnToFaceTolerance or 15",
            indent +
                "self.TurnToFaceToleranceIdle = self.Style.TurnToFaceToleranceIdle or (self.TurnToFaceTolerance / (2))",
            indent + "self.EnterStrafeDist = self.Style.EnterStrafeDist or 5",
            indent + "self.LeaveStrafeDelta = self.Style.LeaveStrafeDelta or 1",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 22));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaCombatInfoAttackZoneReturnFieldsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) !=
                "local r2, r3 = CombatSequences:GetAttackZones(self.Style)" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) !=
                "self.CanCounterAttack = r3" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) !=
                "self.AttackZones = r2") {
            continue;
        }
        (*lines)[line_index] =
            GetLuaLineIndent((*lines)[line_index]) +
            "self.AttackZones, self.CanCounterAttack = CombatSequences:GetAttackZones(self.Style)";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaCombatInfoInnerRingCountCompareForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    for (std::string& line : *lines) {
        if (TrimWhitespace(StripLineComment(line)) !=
            "if 0 < self.Formation.InnerRing:GetNumCombatants() then") {
            continue;
        }
        line = GetLuaLineIndent(line) +
               "if self.Formation.InnerRing:GetNumCombatants() > 0 then";
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaCombatInfoEnrageActionLiteralForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) !=
                "local r1 = {}" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) !=
                "r1.Type = EScriptableAction.ENRAGE" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) !=
                "r1.Anim = self.Style.Enrage.Anim" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 3])) !=
                "r1.EnrageDamageMultiplier = self.Style.Enrage.DamageMultiplier" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 4])) !=
                "r1.EnrageSpeedMultiplier = self.Style.Enrage.SpeedMultiplier" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 5])) != "return r1") {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        const std::string field_indent = indent + "  ";
        std::vector<std::string> replacement = {
            indent + "local r1 = {",
            field_indent + "Type = EScriptableAction.ENRAGE,",
            field_indent + "Anim = self.Style.Enrage.Anim,",
            field_indent +
                "EnrageDamageMultiplier = self.Style.Enrage.DamageMultiplier,",
            field_indent +
                "EnrageSpeedMultiplier = self.Style.Enrage.SpeedMultiplier",
            indent + "}",
            indent + "return r1",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 6));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        return true;
    }

    return false;
}

bool NormalizeRenderedLuaCombatInfoFinalExactShapesForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    changed |= NormalizeRenderedLuaCombatInfoInnerRingCountCompareForParity(lines);
    changed |= NormalizeRenderedLuaCombatInfoEnrageActionLiteralForParity(lines);
    return changed;
}

bool NormalizeRenderedLuaCombatZoneForwardLaddersForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 8 < lines->size(); ++line_index) {
        if (TrimWhitespace(StripLineComment((*lines)[line_index])) !=
                "if dist < self.Style.Limits[CombatZones.Near] then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 1])) !=
                "self.Zone = CombatZones.Near" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 2])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 3])) !=
                "if dist < self.Style.Limits[CombatZones.Middle] then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 4])) !=
                "self.Zone = CombatZones.Middle" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 5])) != "end" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 6])) !=
                "if dist < self.Style.Limits[CombatZones.Far] + self.MovementTolerance then" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 7])) !=
                "self.Zone = CombatZones.Far" ||
            TrimWhitespace(StripLineComment((*lines)[line_index + 8])) != "end") {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        std::vector<std::string> replacement = {
            indent + "if dist < self.Style.Limits[CombatZones.Near] then",
            indent + "self.Zone = CombatZones.Near",
            indent + "elseif dist < self.Style.Limits[CombatZones.Middle] then",
            indent + "self.Zone = CombatZones.Middle",
            indent +
                "elseif dist < self.Style.Limits[CombatZones.Far] + self.MovementTolerance then",
            indent + "self.Zone = CombatZones.Far",
            indent + "end",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 9));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        line_index += replacement.size();
    }

    return changed;
}

bool TryApplyRenderedLuaNearExactInstructionPolarityCleanupWithCallbacks(
    std::vector<std::string>* lines,
    const ExactInstructionMicrodiffCallbacks& callbacks) {
    if (lines == nullptr) {
        return false;
    }
    bool changed = false;
    changed |= refii::lua::SimplifyRenderedLuaConditionHeaders(lines);
    changed |= refii::lua::SimplifyRenderedLuaRedundantGuardConditionParentheses(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanAssertConditions(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanCoercionSelections(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanCoercionReturns(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanLoadPairs(lines);
    changed |= refii::lua::SimplifyRenderedLuaBooleanCarryConditions(lines);
    changed |= NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(lines);
    changed |= ApplyMicrodiffCallback(
        callbacks.try_swap_return_self_right_hand_comparisons, lines);
    changed |= refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(lines);
    changed |= NormalizeRenderedLuaSingleAssertNilPolarityForParity(lines);
    changed |= NormalizeRenderedLuaReturnedLocalTableSelfUsesForParity(lines);
    changed |= NormalizeRenderedLuaFallbackTempAssignmentUsesForParity(lines);
    changed |= NormalizeRenderedLuaBooleanCallArgumentLocalsForParity(lines);
    changed |= NormalizeRenderedLuaEmptyLimitGuardCallDefaultForParity(lines);
    changed |= NormalizeRenderedLuaIndexedStateTableBuildersForParity(lines);
    changed |= NormalizeRenderedLuaInvalidStateDebugMessageForParity(lines);
    changed |= NormalizeRenderedLuaDuplicatedMethodResultChainsForParity(lines);
    changed |= NormalizeRenderedLuaGuardedLocalOrSeedsForParity(lines);
    changed |= NormalizeRenderedLuaImmediateIndexedWriteReseedsForParity(lines);
    changed |= NormalizeRenderedLuaCombatZoneForwardLaddersForParity(lines);
    return changed;
}

bool TryInlineRenderedLuaImmediateSingleUseIndexReturnLocals(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!refii::lua::ParseRenderedAssignmentLine((*lines)[line_index],
                                                     &is_local,
                                                     &lhs_names,
                                                     &rhs_expression) ||
            !is_local || lhs_names.size() != 1) {
            continue;
        }

        const std::string local_name =
            std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
        const std::string rhs_trimmed =
            std::string(refii::lua::TrimLuaWhitespace(rhs_expression));
        if (!refii::lua::IsValidLuaIdentifier(local_name) || rhs_trimmed.empty() ||
            rhs_trimmed.find('\n') != std::string::npos ||
            rhs_trimmed.find(',') != std::string::npos) {
            continue;
        }

        std::size_t return_line = line_index + 1;
        while (return_line < lines->size()) {
            const std::string_view between_trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[return_line]));
            if (!between_trimmed.empty()) {
                break;
            }
            ++return_line;
        }
        if (return_line >= lines->size()) {
            continue;
        }

        const std::string_view return_trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment((*lines)[return_line]));
        if (!return_trimmed.starts_with("return ")) {
            continue;
        }

        const std::string return_expression = std::string(
            refii::lua::TrimLuaWhitespace(return_trimmed.substr(7)));
        if (return_expression.empty() ||
            return_expression.find("[" + local_name + "]") == std::string::npos) {
            continue;
        }

        const auto is_identifier_start = [](char ch) {
            return ch == '_' ||
                   (ch >= 'A' && ch <= 'Z') ||
                   (ch >= 'a' && ch <= 'z');
        };
        const auto is_identifier_char = [&](char ch) {
            return is_identifier_start(ch) || (ch >= '0' && ch <= '9');
        };

        std::size_t local_name_token_count = 0;
        for (std::size_t cursor = 0; cursor < return_expression.size();) {
            if (!is_identifier_start(return_expression[cursor])) {
                ++cursor;
                continue;
            }
            std::size_t end = cursor + 1;
            while (end < return_expression.size() &&
                   is_identifier_char(return_expression[end])) {
                ++end;
            }
            if (std::string_view(return_expression.data() + cursor, end - cursor) ==
                local_name) {
                ++local_name_token_count;
            }
            cursor = end;
        }
        if (local_name_token_count != 1) {
            continue;
        }

        const std::string replacement_expression = "(" + rhs_trimmed + ")";
        std::string rewritten_expression;
        for (std::size_t cursor = 0; cursor < return_expression.size();) {
            if (!is_identifier_start(return_expression[cursor])) {
                rewritten_expression.push_back(return_expression[cursor]);
                ++cursor;
                continue;
            }
            std::size_t end = cursor + 1;
            while (end < return_expression.size() &&
                   is_identifier_char(return_expression[end])) {
                ++end;
            }
            if (std::string_view(return_expression.data() + cursor, end - cursor) ==
                local_name) {
                rewritten_expression += replacement_expression;
            } else {
                rewritten_expression.append(return_expression.substr(cursor, end - cursor));
            }
            cursor = end;
        }
        if (rewritten_expression.empty()) {
            continue;
        }

        const std::string rewritten_line =
            GetLuaLineIndent((*lines)[return_line]) + "return " + rewritten_expression;
        if (rewritten_line == (*lines)[return_line]) {
            continue;
        }

        (*lines)[line_index].clear();
        (*lines)[return_line] = rewritten_line;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_guard_line = [](std::string_view line,
                                     std::string* prefix_out,
                                     std::string* condition_out,
                                     std::string* suffix_out) {
        if (prefix_out == nullptr || condition_out == nullptr || suffix_out == nullptr) {
            return false;
        }
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *prefix_out = "if ";
            *condition_out = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            *suffix_out = " then";
            return true;
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *prefix_out = "elseif ";
            *condition_out = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            *suffix_out = " then";
            return true;
        }
        if (trimmed.starts_with("while ") && trimmed.ends_with(" do")) {
            *prefix_out = "while ";
            *condition_out = std::string(
                refii::lua::TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9)));
            *suffix_out = " do";
            return true;
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string guard_prefix;
        std::string guard_condition;
        std::string guard_suffix;
        if (!parse_guard_line((*lines)[line_index],
                              &guard_prefix,
                              &guard_condition,
                              &guard_suffix)) {
            continue;
        }

        const auto strip_outer_parentheses = [](std::string value) {
            value = std::string(refii::lua::TrimLuaWhitespace(value));
            while (value.size() >= 2 &&
                   value.front() == '(' &&
                   value.back() == ')') {
                value = std::string(refii::lua::TrimLuaWhitespace(
                    std::string_view(value).substr(1, value.size() - 2)));
            }
            return value;
        };

        const std::size_t comparator_position = guard_condition.find(" == ");
        if (comparator_position == std::string::npos ||
            guard_condition.find(" == ", comparator_position + 4) != std::string::npos ||
            guard_condition.find("~=") != std::string::npos ||
            guard_condition.find("<=") != std::string::npos ||
            guard_condition.find(">=") != std::string::npos ||
            guard_condition.find('<') != std::string::npos ||
            guard_condition.find('>') != std::string::npos) {
            continue;
        }

        const std::string lhs = strip_outer_parentheses(
            std::string(guard_condition.substr(0, comparator_position)));
        const std::string rhs = strip_outer_parentheses(
            std::string(guard_condition.substr(comparator_position + 4)));
        if (lhs.empty() || lhs != rhs || !refii::lua::IsValidLuaIdentifier(lhs)) {
            continue;
        }

        std::string alias_source;
        std::size_t looked_back = 0;
        for (std::size_t cursor = line_index; cursor > 0 && looked_back < 4; --cursor) {
            const std::size_t probe = cursor - 1;
            const std::string_view trimmed_probe = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment((*lines)[probe]));
            if (trimmed_probe.empty()) {
                continue;
            }
            ++looked_back;
            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string assign_rhs;
            if (!refii::lua::ParseRenderedAssignmentLine((*lines)[probe],
                                                         &is_local,
                                                         &lhs_names,
                                                         &assign_rhs) ||
                !is_local ||
                lhs_names.size() != 1 ||
                std::string(refii::lua::TrimLuaWhitespace(lhs_names.front())) != lhs) {
                continue;
            }

            const std::string normalized_rhs =
                strip_outer_parentheses(std::string(assign_rhs));
            if (normalized_rhs.empty() ||
                normalized_rhs == lhs ||
                refii::lua::CountIdentifierUses(normalized_rhs, lhs) != 0 ||
                normalized_rhs.find(',') != std::string::npos ||
                normalized_rhs.find(" and ") != std::string::npos ||
                normalized_rhs.find(" or ") != std::string::npos) {
                continue;
            }
            alias_source = normalized_rhs;
            break;
        }
        if (alias_source.empty()) {
            continue;
        }

        const std::string indent = GetLuaLineIndent((*lines)[line_index]);
        (*lines)[line_index] =
            indent + guard_prefix + lhs + " == " + alias_source + guard_suffix;
        changed = true;
    }

    return changed;
}

bool TryApplyRenderedLuaNearExactLocalTimingCleanup(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }
    bool changed = false;
    changed |= refii::lua::LocalizeRenderedLuaFirstAssignments(lines);
    changed |= refii::lua::LocalizeRenderedLuaFunctionLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaBranchShadowedLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaRedundantLocalRedeclarations(lines);
    changed |= refii::lua::SimplifyRenderedLuaIfBranchShadowLocalCarries(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateIdentifierCarryLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateExpressionCarryLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateMemberCarryLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals(lines);
    changed |= refii::lua::SimplifyRenderedLuaScopedSequentialMemberCarries(lines);
    changed |= refii::lua::SimplifyRenderedLuaScopedMemberAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelMemberAliases(lines);
    changed |= refii::lua::SimplifyRenderedLuaTopLevelFunctionAliasAssignments(lines);
    changed |= refii::lua::SimplifyRenderedLuaEscapingGuardedLocalDeclarations(lines);
    changed |= NormalizeRenderedLuaDegenerateSelfCompareAliasGuards(lines);
    changed |= TryInlineRenderedLuaImmediateSingleUseIndexReturnLocals(lines);
    return changed;
}

bool ApplyRenderedLuaNearExactInstructionCleanupBundleWithCallbacks(
    std::vector<std::string>* lines,
    bool include_local_timing,
    bool include_tiny_microdiff,
    bool include_polarity_cleanup,
    const ExactInstructionMicrodiffCallbacks& callbacks) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    if (include_local_timing) {
        changed |= TryApplyRenderedLuaNearExactLocalTimingCleanup(lines);
    }
    if (include_tiny_microdiff) {
        changed |= TryApplyRenderedLuaTinyInstructionMicroDiffCleanupWithCallbacks(
            lines, callbacks);
    }
    changed |= TryApplyRenderedLuaNearExactInstructionMicroDiffCleanupWithCallbacks(
        lines, callbacks);
    if (include_polarity_cleanup) {
        changed |= TryApplyRenderedLuaNearExactInstructionPolarityCleanupWithCallbacks(
            lines, callbacks);
    }
    return changed;
}

}  // namespace refii::lua::internal
