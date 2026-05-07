#include "tools/decompiler/preview_join_render.h"

#include "tools/decompiler/preview_operation_render.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <algorithm>
#include <cstdlib>

namespace refii::lua {
namespace {

constexpr int kLuaMaxPreviewJoinCandidates = 12;
constexpr int kLuaMaxPreviewJoinSpan = 48;

LuaJumpRenderResult ContinueResult(int next_current) {
    return LuaJumpRenderResult{
        .disposition = LuaJumpRenderDisposition::kContinue,
        .next_current = next_current,
    };
}

int GetPreviewJoinSingleWrittenRegister(const LuaPreviewRenderContext& preview_context,
                                        int line) {
    const std::vector<bool> written =
        GetLuaPreviewWrittenRegistersForLine(preview_context, line);
    int written_register = -1;
    for (std::size_t index = 0; index < written.size(); ++index) {
        if (!written[index]) {
            continue;
        }
        if (written_register != -1) {
            return -1;
        }
        written_register = static_cast<int>(index);
    }
    return written_register;
}

bool LooksLikePreviewJoinNumericLiteral(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty()) {
        return false;
    }

    std::string literal(value);
    char* end = nullptr;
    std::strtod(literal.c_str(), &end);
    return end != nullptr && *end == '\0';
}

bool LooksLikePreviewJoinArithmeticExpression(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty()) {
        return false;
    }

    if (value.starts_with('(') && value.ends_with(')')) {
        return LooksLikePreviewJoinArithmeticExpression(
            value.substr(1, value.size() - 2));
    }

    if (value[0] == '-' && !LooksLikePreviewJoinNumericLiteral(value)) {
        return true;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const char ch = value[i];
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
        switch (ch) {
        case '(':
            ++paren_depth;
            break;
        case ')':
            if (paren_depth > 0) {
                --paren_depth;
            }
            break;
        case '{':
            ++brace_depth;
            break;
        case '}':
            if (brace_depth > 0) {
                --brace_depth;
            }
            break;
        case '[':
            ++bracket_depth;
            break;
        case ']':
            if (bracket_depth > 0) {
                --bracket_depth;
            }
            break;
        case '+':
        case '*':
        case '/':
        case '%':
        case '^':
            if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                return true;
            }
            break;
        case '-':
            if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 && i != 0) {
                return true;
            }
            break;
        default:
            break;
        }
    }

    return false;
}

std::string StripPreviewJoinWholeParentheses(std::string_view value) {
    value = TrimLuaWhitespace(value);
    bool changed = true;
    while (changed && value.size() >= 2 && value.front() == '(' && value.back() == ')') {
        changed = false;
        int paren_depth = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool encloses_whole_expression = true;
        for (std::size_t i = 0; i < value.size(); ++i) {
            const char ch = value[i];
            if (in_single_quote) {
                if (ch == '\\' && i + 1 < value.size()) {
                    ++i;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }
            if (in_double_quote) {
                if (ch == '\\' && i + 1 < value.size()) {
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
                if (paren_depth == 0 && i + 1 < value.size()) {
                    encloses_whole_expression = false;
                    break;
                }
            }
        }
        if (!encloses_whole_expression || paren_depth != 0) {
            break;
        }
        value = TrimLuaWhitespace(value.substr(1, value.size() - 2));
        changed = true;
    }
    return std::string(value);
}

bool SplitPreviewJoinTopLevelLogical(std::string_view value,
                                     std::string_view op,
                                     std::string* lhs,
                                     std::string* rhs) {
    if (lhs == nullptr || rhs == nullptr) {
        return false;
    }

    value = TrimLuaWhitespace(value);
    if (value.empty()) {
        return false;
    }

    const std::string token = std::string(" ") + std::string(op) + " ";
    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t i = 0; i + token.size() <= value.size(); ++i) {
        const char ch = value[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
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
        switch (ch) {
        case '(':
            ++paren_depth;
            continue;
        case ')':
            if (paren_depth > 0) {
                --paren_depth;
            }
            continue;
        case '{':
            ++brace_depth;
            continue;
        case '}':
            if (brace_depth > 0) {
                --brace_depth;
            }
            continue;
        case '[':
            ++bracket_depth;
            continue;
        case ']':
            if (bracket_depth > 0) {
                --bracket_depth;
            }
            continue;
        default:
            break;
        }
        if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 &&
            value.substr(i, token.size()) == token) {
            *lhs = StripPreviewJoinWholeParentheses(value.substr(0, i));
            *rhs = StripPreviewJoinWholeParentheses(value.substr(i + token.size()));
            return !lhs->empty() && !rhs->empty();
        }
    }

    return false;
}

bool IsPreviewJoinDefinitelyTruthyExpression(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty() || value == "nil" || value == "false") {
        return false;
    }
    if (value == "true" || LooksLikePreviewJoinNumericLiteral(value) ||
        value.starts_with('"') || value.starts_with('\'') || value.starts_with('{') ||
        value.starts_with("function(") ||
        LooksLikePreviewJoinArithmeticExpression(value)) {
        return true;
    }

    if (value.starts_with('(') && value.ends_with(')')) {
        return IsPreviewJoinDefinitelyTruthyExpression(
            value.substr(1, value.size() - 2));
    }

    return false;
}

enum class LuaPreviewJoinConstantTruthiness {
    kUnknown,
    kFalse,
    kTrue,
};

LuaPreviewJoinConstantTruthiness GetLuaPreviewJoinConstantTruthiness(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty()) {
        return LuaPreviewJoinConstantTruthiness::kUnknown;
    }
    if (value == "nil" || value == "false") {
        return LuaPreviewJoinConstantTruthiness::kFalse;
    }
    if (value == "true" || LooksLikePreviewJoinNumericLiteral(value) ||
        value.starts_with('"') || value.starts_with('\'') || value.starts_with('{') ||
        value.starts_with("function(")) {
        return LuaPreviewJoinConstantTruthiness::kTrue;
    }
    if (value.starts_with("not ")) {
        const LuaPreviewJoinConstantTruthiness operand_truthiness =
            GetLuaPreviewJoinConstantTruthiness(value.substr(4));
        if (operand_truthiness == LuaPreviewJoinConstantTruthiness::kTrue) {
            return LuaPreviewJoinConstantTruthiness::kFalse;
        }
        if (operand_truthiness == LuaPreviewJoinConstantTruthiness::kFalse) {
            return LuaPreviewJoinConstantTruthiness::kTrue;
        }
        return LuaPreviewJoinConstantTruthiness::kUnknown;
    }
    if (value.starts_with('(') && value.ends_with(')')) {
        return GetLuaPreviewJoinConstantTruthiness(value.substr(1, value.size() - 2));
    }
    return LuaPreviewJoinConstantTruthiness::kUnknown;
}

bool HasPreviewJoinRegisterIdentity(const RegisterDeclarations& declarations,
                                    const Lua51Function& function,
                                    int line,
                                    int reg,
                                    bool declared) {
    return reg < function.parameter_count ||
           declared ||
           declarations.Get(reg, std::max(1, line)) != nullptr;
}

bool RepresentsPreviewJoinRegisterIdentity(const StraightLineValue& value,
                                           const RegisterDeclarations& declarations,
                                           const Lua51Function& function,
                                           int line,
                                           int register_count,
                                           int reg) {
    if (!value.valid) {
        return false;
    }

    if (value.multiple_results) {
        return ResolveLuaJoinIdentityRegister(
                   value.text, declarations, function, line, register_count) == reg;
    }

    return NormalizeLuaJoinSafeExpression(
               value.text, declarations, function, line, register_count) ==
           GetStraightLineRegisterName(
               declarations, function, reg, std::max(1, line));
}

bool AreEquivalentPreviewJoinValues(const StraightLineValue& before,
                                    const StraightLineValue& after,
                                    const RegisterDeclarations& declarations,
                                    const Lua51Function& function,
                                    int line,
                                    int register_count) {
    if (before.valid != after.valid || before.multiple_results != after.multiple_results) {
        return false;
    }

    if (before.text == after.text) {
        return true;
    }

    if (!before.valid || !after.valid) {
        return false;
    }

    if (before.multiple_results) {
        const int before_identity = ResolveLuaJoinIdentityRegister(
            before.text, declarations, function, line, register_count);
        const int after_identity = ResolveLuaJoinIdentityRegister(
            after.text, declarations, function, line, register_count);
        return before_identity >= 0 && before_identity == after_identity;
    }

    const std::string before_safe = NormalizeLuaJoinSafeExpression(
        before.text, declarations, function, line, register_count);
    const std::string after_safe = NormalizeLuaJoinSafeExpression(
        after.text, declarations, function, line, register_count);
    return !before_safe.empty() && before_safe == after_safe;
}

bool AreEquivalentPreviewJoinRegisterStates(const StraightLineValue& before,
                                            bool before_has_identity,
                                            const StraightLineValue& after,
                                            bool after_has_identity,
                                            const RegisterDeclarations& declarations,
                                            const Lua51Function& function,
                                            int line,
                                            int register_count,
                                            int reg) {
    if (AreEquivalentPreviewJoinValues(
            before, after, declarations, function, line, register_count)) {
        return true;
    }

    const bool before_identity =
        (!before.valid && before_has_identity) ||
        RepresentsPreviewJoinRegisterIdentity(
            before, declarations, function, line, register_count, reg);
    const bool after_identity =
        (!after.valid && after_has_identity) ||
        RepresentsPreviewJoinRegisterIdentity(
            after, declarations, function, line, register_count, reg);
    return before_identity && after_identity;
}

bool AreEquivalentPreviewJoinSlotStates(const StraightLineValue& before,
                                        bool before_declared,
                                        const StraightLineValue& after,
                                        bool after_declared,
                                        const RegisterDeclarations& declarations,
                                        const Lua51Function& function,
                                        int line,
                                        int register_count,
                                        int reg) {
    if (before_declared != after_declared) {
        return false;
    }

    return AreEquivalentPreviewJoinRegisterStates(
        before,
        HasPreviewJoinRegisterIdentity(declarations, function, line, reg, before_declared),
        after,
        HasPreviewJoinRegisterIdentity(declarations, function, line, reg, after_declared),
        declarations,
        function,
        line,
        register_count,
        reg);
}

bool HasPreviewJoinStableLocalDifference(const LuaBranchStateSnapshot& base_state,
                                         const LuaBranchStateSnapshot& branch_state,
                                         const RegisterDeclarations& declarations,
                                         const Lua51Function& function,
                                         int line,
                                         int register_count) {
    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(base_state.values.size(),
                 std::min(branch_state.values.size(),
                          std::min(base_state.declared.size(), branch_state.declared.size()))));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        const Lua51Declaration* declaration =
            declarations.Get(static_cast<int>(register_index), line);
        const bool is_parameter = static_cast<int>(register_index) < function.parameter_count;
        const bool is_stable_local =
            declaration != nullptr && !declaration->temporary && !declaration->guessed;
        if (!is_parameter && !is_stable_local) {
            continue;
        }

        if (!AreEquivalentPreviewJoinSlotStates(base_state.values[register_index],
                                                base_state.declared[register_index],
                                                branch_state.values[register_index],
                                                branch_state.declared[register_index],
                                                declarations,
                                                function,
                                                line,
                                                register_count,
                                                static_cast<int>(register_index))) {
            return true;
        }
    }

    return false;
}

bool ShouldSkipLuaPreviewNamedRegisterJoin(const RegisterDeclarations& declarations,
                                           const Lua51Function& function,
                                           int register_index,
                                           int line) {
    if (register_index < function.parameter_count) {
        return true;
    }

    const Lua51Declaration* declaration = declarations.Get(register_index, line);
    return declaration != nullptr &&
           !declaration->temporary &&
           !declaration->guessed;
}

}  // namespace

bool TryBuildLuaPreviewSingleRegisterJoinExpression(
    std::string_view condition,
    const std::vector<StraightLineValue>& then_values,
    const std::vector<bool>& then_declared,
    const std::vector<bool>& then_written_registers,
    const std::vector<StraightLineValue>& else_values,
    const std::vector<bool>& else_declared,
    const std::vector<bool>& else_written_registers,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    int line,
    int register_count,
    int* changed_register,
    StraightLineValue* merged_value) {
    if (condition.empty() || changed_register == nullptr || merged_value == nullptr) {
        return false;
    }

    int detected_changed_register = -1;
    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(then_values.size(),
                 std::min(else_values.size(),
                          std::min(then_declared.size(),
                                   std::min(else_declared.size(),
                                            std::min(then_written_registers.size(),
                                                     else_written_registers.size()))))));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        if (AreEquivalentPreviewJoinSlotStates(then_values[register_index],
                                               then_declared[register_index],
                                               else_values[register_index],
                                               else_declared[register_index],
                                               declarations,
                                               function,
                                               line,
                                               register_count,
                                               static_cast<int>(register_index))) {
            continue;
        }
        if (detected_changed_register != -1) {
            return false;
        }
        if ((register_index >= then_written_registers.size() ||
             !then_written_registers[register_index]) &&
            (register_index >= else_written_registers.size() ||
             !else_written_registers[register_index])) {
            return false;
        }
        detected_changed_register = static_cast<int>(register_index);
    }

    if (detected_changed_register < 0) {
        return false;
    }

    const StraightLineValue& then_value =
        then_values[static_cast<std::size_t>(detected_changed_register)];
    const StraightLineValue& else_value =
        else_values[static_cast<std::size_t>(detected_changed_register)];
    if (then_declared[static_cast<std::size_t>(detected_changed_register)] !=
            else_declared[static_cast<std::size_t>(detected_changed_register)] ||
        !then_value.valid || !else_value.valid || then_value.multiple_results ||
        else_value.multiple_results) {
        return false;
    }

    const LuaPreviewJoinConstantTruthiness condition_truthiness =
        GetLuaPreviewJoinConstantTruthiness(condition);
    if (condition_truthiness == LuaPreviewJoinConstantTruthiness::kTrue) {
        *changed_register = detected_changed_register;
        *merged_value = then_value;
        return true;
    }
    if (condition_truthiness == LuaPreviewJoinConstantTruthiness::kFalse) {
        *changed_register = detected_changed_register;
        *merged_value = else_value;
        return true;
    }

    std::string merged_expression;
    std::string then_or_lhs;
    std::string then_or_rhs;
    std::string else_or_lhs;
    std::string else_or_rhs;
    const std::string normalized_then = StripPreviewJoinWholeParentheses(then_value.text);
    const std::string normalized_else = StripPreviewJoinWholeParentheses(else_value.text);
    if (SplitPreviewJoinTopLevelLogical(normalized_then,
                                        "or",
                                        &then_or_lhs,
                                        &then_or_rhs) &&
        StripPreviewJoinWholeParentheses(then_or_rhs) == normalized_else) {
        merged_expression = RenderLogicalExpression(
            RenderLogicalExpression(std::string(condition), "and", then_or_lhs),
            "or",
            then_or_rhs);
    } else if (SplitPreviewJoinTopLevelLogical(normalized_else,
                                               "or",
                                               &else_or_lhs,
                                               &else_or_rhs) &&
               StripPreviewJoinWholeParentheses(else_or_rhs) == normalized_then) {
        merged_expression = RenderLogicalExpression(
            RenderLogicalExpression(RenderUnaryExpression("not", std::string(condition)),
                                    "and",
                                    else_or_lhs),
            "or",
            else_or_rhs);
    } else if (IsPreviewJoinDefinitelyTruthyExpression(then_value.text)) {
        merged_expression = RenderLogicalExpression(
            RenderLogicalExpression(std::string(condition), "and", then_value.text),
            "or",
            else_value.text);
    } else if (IsPreviewJoinDefinitelyTruthyExpression(else_value.text)) {
        merged_expression = RenderLogicalExpression(
            RenderLogicalExpression(RenderUnaryExpression("not", std::string(condition)),
                                    "and",
                                    else_value.text),
            "or",
            then_value.text);
    } else {
        return false;
    }

    *changed_register = detected_changed_register;
    *merged_value = StraightLineValue{
        std::move(merged_expression),
        true,
        false,
    };
    return true;
}

bool ShouldAttemptLuaPreviewStatementJoin(int then_start_line,
                                          int then_end_line,
                                          int else_start_line,
                                          int else_end_line,
                                          int statement_line) {
    if (statement_line < 1) {
        return false;
    }

    int min_line = statement_line;
    int max_line = statement_line;
    const auto consider_range = [&](int start_line, int end_line) {
        if (start_line < 1 || end_line < start_line) {
            return true;
        }
        if (end_line - start_line > kLuaMaxPreviewJoinSpan) {
            return false;
        }
        min_line = std::min(min_line, start_line);
        max_line = std::max(max_line, end_line);
        return true;
    };

    if (!consider_range(then_start_line, then_end_line) ||
        !consider_range(else_start_line, else_end_line)) {
        return false;
    }

    return max_line - min_line <= kLuaMaxPreviewJoinSpan;
}

LuaJumpRenderResult EmitLuaPreviewStatementJoin(std::vector<std::string>* lines,
                                                const LuaPreviewRenderContext& preview_context,
                                                std::vector<StraightLineValue>* values,
                                                std::vector<bool>* declared,
                                                std::string_view condition,
                                                const LuaPreviewStatementJoin& join,
                                                int register_count,
                                                const LuaInvalidateValuesRenderer& invalidate_values,
                                                int invalidate_line,
                                                int next_current) {
    if (lines == nullptr || !invalidate_values) {
        return {};
    }

    std::string then_statement;
    std::string else_statement;
    if (join.then_requires_full_tail_render || join.else_requires_full_tail_render) {
        return {};
    }
    if (!TryRenderLuaPreviewStatementJoin(
            preview_context, join, &then_statement, &else_statement)) {
        return {};
    }
    if (then_statement == else_statement) {
        return {};
    }

    LuaBranchStateSnapshot then_state;
    LuaBranchStateSnapshot else_state;
    const LuaBranchStateSnapshot base_state{
        values != nullptr ? *values : std::vector<StraightLineValue>{},
        declared != nullptr ? *declared : std::vector<bool>{},
    };
    const bool have_join_states =
        values != nullptr &&
        declared != nullptr &&
        TryBuildLuaPreviewStatementJoinStates(preview_context, join, &then_state, &else_state);
    if (values != nullptr && declared != nullptr && !have_join_states) {
        return {};
    }
    if (have_join_states &&
        (HasPreviewJoinStableLocalDifference(base_state,
                                             then_state,
                                             preview_context.declarations,
                                             preview_context.function,
                                             next_current,
                                             register_count) ||
         HasPreviewJoinStableLocalDifference(base_state,
                                             else_state,
                                             preview_context.declarations,
                                             preview_context.function,
                                             next_current,
                                             register_count))) {
        return {};
    }

    AppendIndentedStatement(lines, "  ", "if " + std::string(condition) + " then");
    AppendIndentedStatement(lines, "  ", then_statement);
    AppendIndentedStatement(lines, "  ", "else");
    AppendIndentedStatement(lines, "  ", else_statement);
    AppendIndentedStatement(lines, "  ", "end");
    if (have_join_states) {
        const LuaMergedBranchState merged_state =
            BuildLuaMergedBranchState(base_state,
                                      then_state,
                                      join.then_written_registers,
                                      else_state,
                                      join.else_written_registers,
                                      preview_context.declarations,
                                      preview_context.function,
                                      next_current,
                                      register_count);
        *values = merged_state.values;
        *declared = merged_state.declared;
        return ContinueResult(next_current);
    }

    invalidate_values(invalidate_line);
    return ContinueResult(next_current);
}

LuaJumpRenderResult TryBuildAndEmitLuaPreviewStatementJoin(
    std::vector<std::string>* lines,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int then_end_line,
    int else_start_line,
    int else_end_line,
    int statement_line,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line,
    int next_current) {
    if (!ShouldAttemptLuaPreviewStatementJoin(then_start_line,
                                              then_end_line,
                                              else_start_line,
                                              else_end_line,
                                              statement_line)) {
        return {};
    }

    LuaPreviewStatementJoin join;
    std::string preview_error;
    if (!TryBuildLuaPreviewStatementJoin(preview_context,
                                         values != nullptr ? *values
                                                          : std::vector<StraightLineValue>{},
                                         declared != nullptr ? *declared : std::vector<bool>{},
                                         then_start_line,
                                         then_end_line,
                                         else_start_line,
                                         else_end_line,
                                         statement_line,
                                         &join,
                                         &preview_error)) {
        return {};
    }

    return EmitLuaPreviewStatementJoin(lines,
                                       preview_context,
                                       values,
                                       declared,
                                       condition,
                                       join,
                                       register_count,
                                       invalidate_values,
                                       invalidate_line,
                                       next_current);
}

LuaJumpRenderResult TryBuildAndEmitLuaPreviewStatementJoinToLine(
    std::vector<std::string>* lines,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int else_start_line,
    int join_line,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line,
    int next_current) {
    LuaPreviewStatementJoin join;
    std::string preview_error;
    if (!TryBuildLuaPreviewStatementJoinToLine(preview_context,
                                               values != nullptr ? *values
                                                                : std::vector<StraightLineValue>{},
                                               declared != nullptr ? *declared
                                                                   : std::vector<bool>{},
                                               then_start_line,
                                               else_start_line,
                                               join_line,
                                               &join,
                                               &preview_error)) {
        return {};
    }

    return EmitLuaPreviewStatementJoin(lines,
                                       preview_context,
                                       values,
                                       declared,
                                       condition,
                                       join,
                                       register_count,
                                       invalidate_values,
                                       invalidate_line,
                                       next_current);
}

LuaJumpRenderResult TryApplyLuaPreviewConditionalJoinShortcuts(
    std::vector<std::string>* lines,
    const decompiler::Lua51ControlFlowGraph& control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int branch_target,
    int end_line,
    int instruction_count,
    int register_count,
    const LuaPreviewJoinSideEffectFreeChecker& preview_range_is_side_effect_free,
    const LuaInvalidateValuesRenderer& invalidate_values,
    int invalidate_line) {
    if (lines == nullptr || values == nullptr || declared == nullptr ||
        !preview_range_is_side_effect_free || !invalidate_values) {
        return {};
    }

    const bool branch_target_is_exclusive_else_entry =
        branch_target - 1 >= 1 &&
        static_cast<std::size_t>(branch_target - 1) <= instructions.size() &&
        instructions[static_cast<std::size_t>(branch_target - 2)].opcode ==
            Lua51Opcode::kJmp &&
        [&]() {
            const Lua51Instruction& trailing_then_jump =
                instructions[static_cast<std::size_t>(branch_target - 2)];
            const int raw_target = decompiler::ComputeJumpTargetLine(
                branch_target - 1, trailing_then_jump.sbx, instruction_count);
            const int resolved_target =
                decompiler::ResolveTerminalJumpTarget(instructions, raw_target);
            return raw_target > branch_target || resolved_target > branch_target;
        }();

    if (branch_target == then_start_line + 1 &&
        then_start_line + 2 <= end_line &&
        then_start_line + 2 <= instruction_count &&
        IsLoadBoolPair(instructions, then_start_line)) {
        const LuaJumpRenderResult join_result =
            TryBuildAndEmitLuaPreviewStatementJoin(lines,
                                                  preview_context,
                                                  values,
                                                  declared,
                                                  condition,
                                                  then_start_line,
                                                  then_start_line,
                                                  branch_target,
                                                  branch_target,
                                                  then_start_line + 2,
                                                  register_count,
                                                  invalidate_values,
                                                  invalidate_line,
                                                  then_start_line + 3);
        if (join_result.disposition != LuaJumpRenderDisposition::kError) {
            return join_result;
        }
    }

    if (branch_target > then_start_line) {
        const std::vector<int> join_candidate_lines =
            decompiler::CollectSharedJoinCandidateLines(control_flow_graph,
                                                        then_start_line,
                                                        branch_target,
                                                        branch_target + 1,
                                                        std::min(end_line, instruction_count));
        int attempted_join_candidates = 0;
        for (int join_line : join_candidate_lines) {
            if (attempted_join_candidates >= kLuaMaxPreviewJoinCandidates) {
                break;
            }
            if (!ShouldAttemptLuaPreviewStatementJoin(then_start_line,
                                                     join_line - 1,
                                                     branch_target,
                                                     join_line - 1,
                                                     join_line)) {
                continue;
            }
            ++attempted_join_candidates;
            const LuaJumpRenderResult join_result =
                TryBuildAndEmitLuaPreviewStatementJoinToLine(lines,
                                                             preview_context,
                                                             values,
                                                             declared,
                                                             condition,
                                                             then_start_line,
                                                             branch_target,
                                                             join_line,
                                                             register_count,
                                                             invalidate_values,
                                                             invalidate_line,
                                                             join_line + 1);
            if (join_result.disposition != LuaJumpRenderDisposition::kError) {
                return join_result;
            }
        }
    }

    if (branch_target > then_start_line &&
        branch_target <= end_line &&
        branch_target <= instruction_count &&
        !branch_target_is_exclusive_else_entry &&
        ShouldAttemptLuaPreviewStatementJoin(then_start_line,
                                            branch_target - 1,
                                            branch_target,
                                            branch_target - 1,
                                            branch_target) &&
        preview_range_is_side_effect_free(then_start_line, branch_target - 1)) {
        const LuaJumpRenderResult join_result =
            TryBuildAndEmitLuaPreviewStatementJoin(lines,
                                                  preview_context,
                                                  values,
                                                  declared,
                                                  condition,
                                                  then_start_line,
                                                  branch_target - 1,
                                                  branch_target,
                                                  branch_target - 1,
                                                  branch_target,
                                                  register_count,
                                                  invalidate_values,
                                                  invalidate_line,
                                                  branch_target + 1);
        if (join_result.disposition != LuaJumpRenderDisposition::kError) {
            return join_result;
        }
    }

    return {};
}

LuaJumpRenderResult TryApplyLuaPreviewTruthyValueJoinShortcut(
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int branch_target,
    int instruction_count,
    int register_count) {
    if (values == nullptr || declared == nullptr || condition.empty() ||
        then_start_line < 1 || branch_target <= then_start_line ||
        static_cast<std::size_t>(branch_target) > preview_context.instructions.size()) {
        return {};
    }

    const int then_test_line = then_start_line + 1;
    const int then_jump_line = then_start_line + 2;
    const int join_line = branch_target + 1;
    if (then_jump_line >= join_line || join_line > instruction_count ||
        static_cast<std::size_t>(then_jump_line) > preview_context.instructions.size()) {
        return {};
    }

    const Lua51Instruction& then_test =
        preview_context.instructions[static_cast<std::size_t>(then_test_line - 1)];
    const Lua51Instruction& then_jump =
        preview_context.instructions[static_cast<std::size_t>(then_jump_line - 1)];
    if (then_test.opcode != Lua51Opcode::kTest || then_jump.opcode != Lua51Opcode::kJmp ||
        decompiler::ComputeJumpTargetLine(then_jump_line,
                                          then_jump.sbx,
                                          instruction_count) != join_line) {
        return {};
    }

    const int then_register =
        GetPreviewJoinSingleWrittenRegister(preview_context, then_start_line);
    const int else_register =
        GetPreviewJoinSingleWrittenRegister(preview_context, branch_target);
    if (then_register < 0 || then_register != else_register ||
        then_test.a != then_register || then_test.c != 1) {
        return {};
    }

    std::vector<StraightLineValue> then_values = *values;
    std::vector<StraightLineValue> else_values = *values;
    std::vector<bool> then_declared = *declared;
    std::vector<bool> else_declared = *declared;
    LuaPreviewOperationContext then_operation_context{
        .instructions = preview_context.instructions,
        .declarations = preview_context.declarations,
        .function = preview_context.function,
        .register_count = register_count,
        .values = &then_values,
        .declared = &then_declared,
        .call_arguments_for = preview_context.call_arguments_for,
        .render_child = preview_context.render_child,
    };
    LuaPreviewOperationContext else_operation_context{
        .instructions = preview_context.instructions,
        .declarations = preview_context.declarations,
        .function = preview_context.function,
        .register_count = register_count,
        .values = &else_values,
        .declared = &else_declared,
        .call_arguments_for = preview_context.call_arguments_for,
        .render_child = preview_context.render_child,
    };

    auto instruction_line = [&](int pc) {
        const Lua51Instruction& instruction =
            preview_context.instructions[static_cast<std::size_t>(pc - 1)];
        return std::max(1, static_cast<int>(instruction.line == 0 ? pc : instruction.line));
    };

    int next_current = then_start_line + 1;
    LuaPreviewOperationDisposition disposition =
        LuaPreviewOperationDisposition::kBuildFailure;
    std::string build_error;
    if (!TryApplyLuaPreviewOperation(then_operation_context,
                                     then_start_line,
                                     instruction_line(then_start_line),
                                     LuaPreviewOperationMode::kStateTracking,
                                     &disposition,
                                     &build_error,
                                     &next_current)) {
        return {};
    }

    next_current = branch_target + 1;
    disposition = LuaPreviewOperationDisposition::kBuildFailure;
    build_error.clear();
    if (!TryApplyLuaPreviewOperation(else_operation_context,
                                     branch_target,
                                     instruction_line(branch_target),
                                     LuaPreviewOperationMode::kStateTracking,
                                     &disposition,
                                     &build_error,
                                     &next_current)) {
        return {};
    }

    std::vector<bool> then_written_registers(static_cast<std::size_t>(register_count), false);
    std::vector<bool> else_written_registers(static_cast<std::size_t>(register_count), false);
    then_written_registers[static_cast<std::size_t>(then_register)] = true;
    else_written_registers[static_cast<std::size_t>(else_register)] = true;

    FinalizeLuaJoinCandidateState(&then_values,
                                  &then_declared,
                                  *values,
                                  *declared,
                                  then_written_registers,
                                  preview_context.declarations,
                                  preview_context.function,
                                  join_line,
                                  register_count);
    FinalizeLuaJoinCandidateState(&else_values,
                                  &else_declared,
                                  *values,
                                  *declared,
                                  else_written_registers,
                                  preview_context.declarations,
                                  preview_context.function,
                                  join_line,
                                  register_count);

    int changed_register = -1;
    StraightLineValue merged_value;
    if (!TryBuildLuaPreviewSingleRegisterJoinExpression(condition,
                                                        then_values,
                                                        then_declared,
                                                        then_written_registers,
                                                        else_values,
                                                        else_declared,
                                                        else_written_registers,
                                                        preview_context.declarations,
                                                        preview_context.function,
                                                        join_line,
                                                        register_count,
                                                        &changed_register,
                                                        &merged_value)) {
        return {};
    }

    if (ShouldSkipLuaPreviewNamedRegisterJoin(preview_context.declarations,
                                              preview_context.function,
                                              changed_register,
                                              join_line)) {
        return {};
    }

    *values = then_values;
    *declared = then_declared;
    (*values)[static_cast<std::size_t>(changed_register)] = std::move(merged_value);
    return ContinueResult(join_line);
}

LuaJumpRenderResult TryApplyLuaPreviewRegisterStateJoinShortcut(
    const decompiler::Lua51ControlFlowGraph& control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int branch_target,
    int end_line,
    int instruction_count,
    int register_count,
    const LuaPreviewJoinSideEffectFreeChecker& preview_range_is_side_effect_free) {
    if (values == nullptr || declared == nullptr || condition.empty() ||
        !preview_range_is_side_effect_free || branch_target <= then_start_line ||
        branch_target > instruction_count) {
        return {};
    }

    const LuaJumpRenderResult truthy_value_result =
        TryApplyLuaPreviewTruthyValueJoinShortcut(preview_context,
                                                  values,
                                                  declared,
                                                  condition,
                                                  then_start_line,
                                                  branch_target,
                                                  instruction_count,
                                                  register_count);
    if (truthy_value_result.disposition != LuaJumpRenderDisposition::kError) {
        return truthy_value_result;
    }

    const int max_join_line = std::min(instruction_count,
                                       std::max(end_line + 1,
                                                then_start_line + kLuaMaxPreviewJoinSpan));
    if (branch_target > max_join_line) {
        return {};
    }

    const std::vector<int> join_candidate_lines =
        decompiler::CollectSharedJoinCandidateLines(control_flow_graph,
                                                    then_start_line,
                                                    branch_target,
                                                    branch_target,
                                                    max_join_line);
    int attempted_join_candidates = 0;
    for (int join_line : join_candidate_lines) {
        if (attempted_join_candidates >= kLuaMaxPreviewJoinCandidates) {
            break;
        }
        ++attempted_join_candidates;

        if (join_line < branch_target) {
            continue;
        }
        if (join_line - then_start_line > kLuaMaxPreviewJoinSpan ||
            join_line - branch_target > kLuaMaxPreviewJoinSpan) {
            continue;
        }
        if (then_start_line <= join_line - 1 &&
            !preview_range_is_side_effect_free(then_start_line, join_line - 1)) {
            continue;
        }
        if (branch_target <= join_line - 1 &&
            !preview_range_is_side_effect_free(branch_target, join_line - 1)) {
            continue;
        }

        std::vector<StraightLineValue> then_values = *values;
        std::vector<StraightLineValue> else_values = *values;
        std::vector<bool> then_declared = *declared;
        std::vector<bool> else_declared = *declared;
        std::string preview_error;
        if (!EvaluateLuaBranchPathToJoin(preview_context,
                                         then_start_line,
                                         join_line,
                                         &then_values,
                                         &then_declared,
                                         &preview_error) ||
            !EvaluateLuaBranchPathToJoin(preview_context,
                                         branch_target,
                                         join_line,
                                         &else_values,
                                         &else_declared,
                                         &preview_error)) {
            continue;
        }

        const std::vector<bool> then_written_registers = CollectWrittenLuaRegistersInRange(
            instructions, register_count, then_start_line, join_line - 1);
        const std::vector<bool> else_written_registers = CollectWrittenLuaRegistersInRange(
            instructions, register_count, branch_target, join_line - 1);
        FinalizeLuaJoinCandidateState(&then_values,
                                      &then_declared,
                                      *values,
                                      *declared,
                                      then_written_registers,
                                      preview_context.declarations,
                                      preview_context.function,
                                      join_line,
                                      register_count);
        FinalizeLuaJoinCandidateState(&else_values,
                                      &else_declared,
                                      *values,
                                      *declared,
                                      else_written_registers,
                                      preview_context.declarations,
                                      preview_context.function,
                                      join_line,
                                      register_count);

        int changed_register = -1;
        StraightLineValue merged_value;
        if (!TryBuildLuaPreviewSingleRegisterJoinExpression(condition,
                                                            then_values,
                                                            then_declared,
                                                            then_written_registers,
                                                            else_values,
                                                            else_declared,
                                                            else_written_registers,
                                                            preview_context.declarations,
                                                            preview_context.function,
                                                            join_line,
                                                            register_count,
                                                            &changed_register,
                                                            &merged_value)) {
            continue;
        }

        if (ShouldSkipLuaPreviewNamedRegisterJoin(preview_context.declarations,
                                                  preview_context.function,
                                                  changed_register,
                                                  join_line)) {
            continue;
        }

        *values = then_values;
        *declared = then_declared;
        (*values)[static_cast<std::size_t>(changed_register)] = std::move(merged_value);
        return ContinueResult(join_line);
    }

    return {};
}

LuaJumpRenderResult TryApplyLuaPreviewCallJoinShortcut(
    std::vector<std::string>* lines,
    const std::vector<Lua51Instruction>& instructions,
    const LuaPreviewRenderContext& preview_context,
    std::vector<StraightLineValue>* values,
    std::vector<bool>* declared,
    std::string_view condition,
    int then_start_line,
    int branch_target,
    int end_line,
    int instruction_count,
    int register_count,
    const LuaInvalidateValuesRenderer& invalidate_values) {
    if (lines == nullptr || values == nullptr || declared == nullptr || !invalidate_values) {
        return {};
    }

    if (branch_target <= then_start_line ||
        branch_target > end_line ||
        branch_target > instruction_count) {
        return {};
    }

    int then_jump_line = -1;
    int call_line = -1;
    const auto compute_jump_target = [&](int pc, int sbx) {
        return decompiler::ComputeJumpTargetLine(pc, sbx, instruction_count);
    };

    for (int candidate = then_start_line; candidate <= branch_target - 1; ++candidate) {
        const Lua51Instruction& maybe_then_jump =
            instructions[static_cast<std::size_t>(candidate - 1)];
        if (maybe_then_jump.opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const int candidate_call_line =
            compute_jump_target(candidate, maybe_then_jump.sbx);
        if (candidate_call_line <= branch_target ||
            candidate_call_line > end_line ||
            candidate_call_line > instruction_count) {
            continue;
        }

        const Lua51Instruction& maybe_call =
            instructions[static_cast<std::size_t>(candidate_call_line - 1)];
        if (maybe_call.opcode != Lua51Opcode::kCall &&
            maybe_call.opcode != Lua51Opcode::kTailCall) {
            continue;
        }

        then_jump_line = candidate;
        call_line = candidate_call_line;
        break;
    }

    if (then_jump_line == -1 || call_line == -1 || branch_target > call_line - 1) {
        return {};
    }

    const int then_preview_end = then_jump_line - 1;
    return TryBuildAndEmitLuaPreviewStatementJoin(lines,
                                                  preview_context,
                                                  values,
                                                  declared,
                                                  condition,
                                                  then_start_line,
                                                  then_preview_end,
                                                  branch_target,
                                                  call_line - 1,
                                                  call_line,
                                                  register_count,
                                                  invalidate_values,
                                                  call_line,
                                                  call_line + 1);
}

}  // namespace refii::lua
