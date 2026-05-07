#include "tools/decompiler/rendered/bytecode_parity_repair_parsing.h"

#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <cctype>

namespace refii::lua::internal {

bool TryExtractBytecodeParityQuotedLiteral(std::string_view text,
                                           std::size_t* offset,
                                           std::string* literal) {
    if (offset == nullptr || literal == nullptr) {
        return false;
    }
    std::size_t index = text.find('"', *offset);
    if (index == std::string_view::npos) {
        return false;
    }

    std::string parsed;
    parsed.push_back('"');
    bool escaped = false;
    for (++index; index < text.size(); ++index) {
        const char ch = text[index];
        parsed.push_back(ch);
        if (escaped) {
            escaped = false;
            continue;
        }
        if (ch == '\\') {
            escaped = true;
            continue;
        }
        if (ch == '"') {
            *offset = index + 1;
            *literal = std::move(parsed);
            return true;
        }
    }
    return false;
}

bool TryExtractBytecodeParityQuotedLiteralPair(std::string_view note,
                                               std::string_view marker,
                                               std::string* expected_literal,
                                               std::string* actual_literal) {
    if (expected_literal == nullptr || actual_literal == nullptr) {
        return false;
    }
    const std::size_t marker_pos = note.find(marker);
    if (marker_pos == std::string_view::npos) {
        return false;
    }
    std::size_t section_end = note.find(';', marker_pos);
    if (section_end == std::string_view::npos) {
        section_end = note.size();
    }
    const std::string_view section = note.substr(marker_pos, section_end - marker_pos);

    std::size_t offset = 0;
    std::string first;
    std::string second;
    if (!TryExtractBytecodeParityQuotedLiteral(section, &offset, &first) ||
        !TryExtractBytecodeParityQuotedLiteral(section, &offset, &second) ||
        first == second) {
        return false;
    }

    *expected_literal = std::move(first);
    *actual_literal = std::move(second);
    return true;
}

std::string StripBytecodeRepairWholeParens(std::string_view value) {
    value = TrimWhitespace(value);
    while (value.size() >= 2 && value.front() == '(' && value.back() == ')') {
        int depth = 0;
        bool balanced_outer = true;
        for (std::size_t index = 0; index < value.size(); ++index) {
            if (value[index] == '(') {
                ++depth;
            } else if (value[index] == ')') {
                --depth;
                if (depth == 0 && index + 1 < value.size()) {
                    balanced_outer = false;
                    break;
                }
            }
            if (depth < 0) {
                balanced_outer = false;
                break;
            }
        }
        if (!balanced_outer || depth != 0) {
            break;
        }
        value = TrimWhitespace(value.substr(1, value.size() - 2));
    }
    return std::string(value);
}

bool IsBytecodeRepairSafeDottedPrefix(std::string_view value) {
    value = TrimWhitespace(value);
    if (value.empty() || value.front() == '.' || value.back() == '.') {
        return false;
    }

    bool expect_identifier_start = true;
    for (const char ch : value) {
        const bool identifier =
            std::isalnum(static_cast<unsigned char>(ch)) != 0 || ch == '_';
        if (expect_identifier_start) {
            if (!(std::isalpha(static_cast<unsigned char>(ch)) != 0 || ch == '_')) {
                return false;
            }
            expect_identifier_start = false;
        } else if (ch == '.') {
            expect_identifier_start = true;
        } else if (!identifier) {
            return false;
        }
    }
    return !expect_identifier_start;
}

std::vector<std::string> SplitBytecodeRepairTopLevelArgs(std::string_view body) {
    std::vector<std::string> args;
    std::size_t arg_start = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t index = 0; index <= body.size(); ++index) {
        const char ch = index < body.size() ? body[index] : ',';
        if (index < body.size()) {
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
            if (ch == ')' && paren_depth > 0) {
                --paren_depth;
                continue;
            }
            if (ch == '{') {
                ++brace_depth;
                continue;
            }
            if (ch == '}' && brace_depth > 0) {
                --brace_depth;
                continue;
            }
            if (ch == '[') {
                ++bracket_depth;
                continue;
            }
            if (ch == ']' && bracket_depth > 0) {
                --bracket_depth;
                continue;
            }
        }
        if (ch != ',' || in_single_quote || in_double_quote ||
            paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
            continue;
        }
        const std::string_view arg =
            TrimWhitespace(body.substr(arg_start, index - arg_start));
        if (!arg.empty()) {
            args.emplace_back(arg);
        }
        arg_start = index + 1;
    }
    return args;
}

bool IsBytecodeRepairSimpleCalleePath(std::string_view callee) {
    callee = TrimWhitespace(callee);
    if (callee.empty() ||
        callee.find_first_of(" \t{}[]") != std::string_view::npos) {
        return false;
    }
    std::size_t token_start = 0;
    while (token_start < callee.size()) {
        const std::size_t split = callee.find_first_of(".:", token_start);
        const std::string_view token =
            split == std::string_view::npos
                ? callee.substr(token_start)
                : callee.substr(token_start, split - token_start);
        if (!refii::lua::IsValidLuaIdentifier(TrimWhitespace(token))) {
            return false;
        }
        if (split == std::string_view::npos) {
            break;
        }
        token_start = split + 1;
    }
    return true;
}

bool IsBytecodeRepairSimpleCallExpression(std::string_view expression) {
    expression = TrimWhitespace(expression);
    if (expression.empty() || expression.back() != ')') {
        return false;
    }
    const std::size_t open_pos = expression.find('(');
    if (open_pos == std::string_view::npos || open_pos == 0) {
        return false;
    }
    const std::string callee =
        std::string(TrimWhitespace(expression.substr(0, open_pos)));
    return IsBytecodeRepairSimpleCalleePath(callee);
}

bool BytecodeRepairSourceContainsToken(const std::vector<std::string>& source_lines,
                                       std::string_view token) {
    for (const std::string& line : source_lines) {
        bool found = false;
        ForEachLuaIdentifierToken(
            StripLineComment(line),
            LuaTokenScanOptions{},
            [&](std::string_view candidate, std::size_t, std::size_t) {
                if (candidate == token) {
                    found = true;
                }
            });
        if (found) {
            return true;
        }
    }
    return false;
}

std::string BytecodeRepairCamelToSnake(std::string_view value) {
    std::string result;
    for (std::size_t index = 0; index < value.size(); ++index) {
        const char ch = value[index];
        if (std::isupper(static_cast<unsigned char>(ch)) != 0) {
            if (!result.empty()) {
                result.push_back('_');
            }
            result.push_back(
                static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
        } else {
            result.push_back(ch);
        }
    }
    return result;
}

std::string InferBytecodeRepairTempNameFromCall(std::string_view expression) {
    expression = TrimWhitespace(expression);
    const std::size_t open_pos = expression.find('(');
    if (open_pos == std::string_view::npos) {
        return {};
    }
    std::string callee =
        std::string(TrimWhitespace(expression.substr(0, open_pos)));
    const std::size_t member_pos = callee.find_last_of(".:");
    if (member_pos != std::string::npos) {
        callee.erase(0, member_pos + 1);
    }
    constexpr std::string_view kGetScriptPrefix = "GetScript";
    constexpr std::string_view kGetPrefix = "Get";
    if (StartsWith(callee, kGetScriptPrefix) &&
        callee.size() > kGetScriptPrefix.size()) {
        const std::string inferred =
            BytecodeRepairCamelToSnake(std::string_view(callee).substr(kGetScriptPrefix.size()));
        return refii::lua::IsValidLuaIdentifier(inferred) ? inferred : std::string();
    }
    if (StartsWith(callee, kGetPrefix) && callee.size() > kGetPrefix.size()) {
        const std::string inferred =
            BytecodeRepairCamelToSnake(std::string_view(callee).substr(kGetPrefix.size()));
        return refii::lua::IsValidLuaIdentifier(inferred) ? inferred : std::string();
    }
    return {};
}

std::string ChooseBytecodeRepairTempName(const std::vector<std::string>& source_lines,
                                         std::string_view expression,
                                         bool prefer_call_name) {
    if (prefer_call_name) {
        const std::string inferred = InferBytecodeRepairTempNameFromCall(expression);
        if (!inferred.empty()) {
            return inferred;
        }
        static constexpr std::string_view preferred_names[] = {
            "move_to_pos",
            "result",
            "r1",
            "r2",
            "r3",
            "r4",
            "r5",
        };
        for (std::string_view preferred : preferred_names) {
            if (!BytecodeRepairSourceContainsToken(source_lines, preferred)) {
                return std::string(preferred);
            }
        }
    }
    for (int index = 1; index <= 32; ++index) {
        const std::string candidate = "r" + std::to_string(index);
        if (!BytecodeRepairSourceContainsToken(source_lines, candidate)) {
            return candidate;
        }
    }
    return std::string("r33");
}

bool ParseBytecodeRepairSingleAssignment(std::string_view line,
                                         bool* is_local,
                                         std::string* lhs_out,
                                         std::string* rhs_out) {
    std::vector<std::string> lhs_names;
    std::string rhs;
    if (!refii::lua::ParseRenderedAssignmentLine(line, is_local, &lhs_names, &rhs) ||
        lhs_names.size() != 1) {
        return false;
    }
    if (lhs_out != nullptr) {
        *lhs_out = std::string(refii::lua::TrimLuaWhitespace(lhs_names.front()));
    }
    if (rhs_out != nullptr) {
        *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs));
    }
    return true;
}

bool ParseBytecodeRepairPlainAssignment(std::string_view line,
                                        bool* is_local,
                                        std::string* lhs_out,
                                        std::string* rhs_out) {
    if (ParseBytecodeRepairSingleAssignment(line, is_local, lhs_out, rhs_out)) {
        return true;
    }

    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    const bool local_assignment = StartsWith(trimmed, "local ");
    const std::string_view body =
        local_assignment ? TrimWhitespace(trimmed.substr(6)) : trimmed;
    const std::size_t equals_pos = body.find(" = ");
    if (equals_pos == std::string_view::npos || equals_pos == 0) {
        return false;
    }
    const std::string lhs =
        std::string(TrimWhitespace(body.substr(0, equals_pos)));
    const std::string rhs =
        std::string(TrimWhitespace(body.substr(equals_pos + 3)));
    if (lhs.empty() || rhs.empty() ||
        lhs.find(',') != std::string::npos) {
        return false;
    }
    if (is_local != nullptr) {
        *is_local = local_assignment;
    }
    if (lhs_out != nullptr) {
        *lhs_out = lhs;
    }
    if (rhs_out != nullptr) {
        *rhs_out = rhs;
    }
    return true;
}

bool ParseBytecodeRepairAssignmentList(std::string_view line,
                                       bool* is_local_out,
                                       std::vector<std::string>* lhs_names_out,
                                       std::string* rhs_out) {
    if (is_local_out == nullptr || lhs_names_out == nullptr || rhs_out == nullptr) {
        return false;
    }

    bool is_local = false;
    std::vector<std::string> lhs_names;
    std::string rhs;
    if (!refii::lua::ParseRenderedAssignmentLine(std::string(line),
                                                 &is_local,
                                                 &lhs_names,
                                                 &rhs) ||
        lhs_names.empty()) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        is_local = StartsWith(trimmed, "local ");
        const std::string_view body =
            is_local ? TrimWhitespace(trimmed.substr(6)) : trimmed;
        const std::size_t equals_pos = body.find(" = ");
        if (equals_pos == std::string_view::npos || equals_pos == 0) {
            return false;
        }

        lhs_names.clear();
        std::string_view lhs_body = body.substr(0, equals_pos);
        while (!lhs_body.empty()) {
            const std::size_t comma_pos = lhs_body.find(',');
            const std::string_view lhs =
                TrimWhitespace(comma_pos == std::string_view::npos
                                   ? lhs_body
                                   : lhs_body.substr(0, comma_pos));
            if (lhs.empty()) {
                return false;
            }
            lhs_names.emplace_back(lhs);
            if (comma_pos == std::string_view::npos) {
                break;
            }
            lhs_body = lhs_body.substr(comma_pos + 1);
        }
        rhs = std::string(TrimWhitespace(body.substr(equals_pos + 3)));
    }

    for (std::string& lhs : lhs_names) {
        lhs = std::string(refii::lua::TrimLuaWhitespace(lhs));
        if (!IsBytecodeRepairSafeDottedPrefix(lhs)) {
            return false;
        }
    }

    *is_local_out = is_local;
    *lhs_names_out = std::move(lhs_names);
    *rhs_out = std::string(refii::lua::TrimLuaWhitespace(rhs));
    return !rhs_out->empty();
}

bool ParseBytecodeRepairLocalInitializer(std::string_view line,
                                         std::string* lhs_out,
                                         std::string* rhs_out) {
    bool is_local = false;
    std::string lhs;
    std::string rhs;
    if (ParseBytecodeRepairSingleAssignment(line, &is_local, &lhs, &rhs) && is_local) {
        if (lhs_out != nullptr) {
            *lhs_out = lhs;
        }
        if (rhs_out != nullptr) {
            *rhs_out = rhs;
        }
        return true;
    }

    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    constexpr std::string_view kLocalPrefix = "local ";
    const std::size_t equals_pos = trimmed.find(" = ");
    if (!StartsWith(trimmed, kLocalPrefix) ||
        equals_pos == std::string_view::npos ||
        equals_pos <= kLocalPrefix.size()) {
        return false;
    }
    if (lhs_out != nullptr) {
        *lhs_out = std::string(TrimWhitespace(
            trimmed.substr(kLocalPrefix.size(),
                           equals_pos - kLocalPrefix.size())));
    }
    if (rhs_out != nullptr) {
        *rhs_out = std::string(TrimWhitespace(trimmed.substr(equals_pos + 3)));
    }
    return true;
}

std::size_t FindBytecodeRepairTopLevelToken(std::string_view value,
                                            std::string_view token) {
    if (token.empty() || value.size() < token.size()) {
        return std::string_view::npos;
    }

    int paren_depth = 0;
    int bracket_depth = 0;
    int brace_depth = 0;
    char quote = '\0';
    bool escaped = false;
    for (std::size_t index = 0; index + token.size() <= value.size(); ++index) {
        const char ch = value[index];
        if (quote != '\0') {
            if (escaped) {
                escaped = false;
            } else if (ch == '\\') {
                escaped = true;
            } else if (ch == quote) {
                quote = '\0';
            }
            continue;
        }

        if (ch == '\'' || ch == '"') {
            quote = ch;
            continue;
        }
        if (ch == '(') {
            ++paren_depth;
            continue;
        }
        if (ch == ')') {
            paren_depth = std::max(0, paren_depth - 1);
            continue;
        }
        if (ch == '[') {
            ++bracket_depth;
            continue;
        }
        if (ch == ']') {
            bracket_depth = std::max(0, bracket_depth - 1);
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            brace_depth = std::max(0, brace_depth - 1);
            continue;
        }
        if (paren_depth == 0 && bracket_depth == 0 && brace_depth == 0 &&
            value.substr(index, token.size()) == token) {
            return index;
        }
    }
    return std::string_view::npos;
}

bool ParseBytecodeRepairNotOrNotAndCondition(std::string_view condition,
                                             std::string* positive_lhs,
                                             std::string* positive_rhs,
                                             std::string* carry_condition) {
    const std::string stripped =
        StripBytecodeRepairWholeParens(condition);
    const std::size_t and_pos =
        FindBytecodeRepairTopLevelToken(stripped, " and ");
    if (and_pos == std::string::npos) {
        return false;
    }

    const std::string not_or_not =
        StripBytecodeRepairWholeParens(stripped.substr(0, and_pos));
    const std::string rhs =
        StripBytecodeRepairWholeParens(stripped.substr(and_pos + 5));
    const std::size_t or_pos =
        FindBytecodeRepairTopLevelToken(not_or_not, " or ");
    if (or_pos == std::string::npos || rhs.empty()) {
        return false;
    }

    const std::string first_negated =
        std::string(TrimWhitespace(not_or_not.substr(0, or_pos)));
    const std::string second_negated =
        std::string(TrimWhitespace(not_or_not.substr(or_pos + 4)));
    constexpr std::string_view kNotPrefix = "not ";
    if (!StartsWith(first_negated, kNotPrefix) ||
        !StartsWith(second_negated, kNotPrefix)) {
        return false;
    }

    const std::string first_positive =
        StripBytecodeRepairWholeParens(first_negated.substr(kNotPrefix.size()));
    const std::string second_positive =
        StripBytecodeRepairWholeParens(second_negated.substr(kNotPrefix.size()));
    if (first_positive.empty() || second_positive.empty()) {
        return false;
    }

    if (positive_lhs != nullptr) {
        *positive_lhs = first_positive;
    }
    if (positive_rhs != nullptr) {
        *positive_rhs = second_positive;
    }
    if (carry_condition != nullptr) {
        *carry_condition = rhs;
    }
    return true;
}

bool SplitBytecodeRepairTopLevelBooleanParts(std::string_view condition,
                                             std::string_view op,
                                             std::vector<std::string>* parts) {
    if (parts == nullptr || op.empty()) {
        return false;
    }

    condition = TrimWhitespace(condition);
    parts->clear();
    if (condition.empty()) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    std::size_t part_begin = 0;
    bool found = false;
    for (std::size_t index = 0; index < condition.size(); ++index) {
        const char ch = condition[index];
        if (in_single_quote) {
            if (ch == '\\' && index + 1 < condition.size()) {
                ++index;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && index + 1 < condition.size()) {
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
            if (paren_depth > 0) {
                --paren_depth;
            }
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            if (brace_depth > 0) {
                --brace_depth;
            }
            continue;
        }
        if (ch == '[') {
            ++bracket_depth;
            continue;
        }
        if (ch == ']') {
            if (bracket_depth > 0) {
                --bracket_depth;
            }
            continue;
        }
        if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
            continue;
        }
        if (index + op.size() <= condition.size() &&
            condition.substr(index, op.size()) == op &&
            (index == 0 ||
             std::isspace(static_cast<unsigned char>(condition[index - 1])) != 0) &&
            (index + op.size() >= condition.size() ||
             std::isspace(static_cast<unsigned char>(condition[index + op.size()])) != 0)) {
            parts->push_back(
                std::string(TrimWhitespace(condition.substr(part_begin, index - part_begin))));
            part_begin = index + op.size();
            found = true;
            index += op.size() - 1;
        }
    }
    if (!found) {
        return false;
    }
    parts->push_back(std::string(TrimWhitespace(condition.substr(part_begin))));
    return !parts->empty();
}

bool TryExtractBytecodeRepairIfCondition(std::string_view line,
                                         std::string* indent_out,
                                         std::string* condition_out) {
    if (indent_out == nullptr || condition_out == nullptr) {
        return false;
    }

    const std::string_view stripped = StripLineComment(line);
    const std::string_view trimmed = TrimWhitespace(stripped);
    if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then")) {
        return false;
    }

    const std::size_t indent_size =
        static_cast<std::size_t>(trimmed.data() - stripped.data());
    *indent_out = std::string(stripped.substr(0, indent_size));
    *condition_out = std::string(TrimWhitespace(trimmed.substr(3, trimmed.size() - 8)));
    return !condition_out->empty();
}

std::size_t FindBytecodeRepairMatchingBrace(std::string_view line,
                                            std::size_t open_pos) {
    int depth = 0;
    bool in_string = false;
    char quote = '\0';
    for (std::size_t index = open_pos; index < line.size(); ++index) {
        const char ch = line[index];
        if (in_string) {
            if (ch == quote && (index == 0 || line[index - 1] != '\\')) {
                in_string = false;
            }
            continue;
        }
        if (ch == '"' || ch == '\'') {
            in_string = true;
            quote = ch;
            continue;
        }
        if (ch == '{') {
            ++depth;
        } else if (ch == '}') {
            --depth;
            if (depth == 0) {
                return index;
            }
        }
    }
    return std::string_view::npos;
}

bool ParseBytecodeRepairTableLiteralFields(
    std::string_view literal,
    std::vector<BytecodeRepairParsedTableField>* fields_out) {
    if (fields_out == nullptr) {
        return false;
    }
    fields_out->clear();
    literal = TrimWhitespace(literal);
    if (literal.size() < 5 || literal.front() != '{' || literal.back() != '}') {
        return false;
    }
    const std::string_view body =
        TrimWhitespace(literal.substr(1, literal.size() - 2));
    if (body.empty()) {
        return false;
    }

    std::vector<std::string_view> parts;
    bool in_string = false;
    char quote = '\0';
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    std::size_t start = 0;
    for (std::size_t index = 0; index <= body.size(); ++index) {
        const bool at_end = index == body.size();
        const char ch = at_end ? ',' : body[index];
        if (!at_end && in_string) {
            if (ch == quote && (index == 0 || body[index - 1] != '\\')) {
                in_string = false;
            }
            continue;
        }
        if (!at_end && (ch == '"' || ch == '\'')) {
            in_string = true;
            quote = ch;
            continue;
        }
        if (!at_end) {
            if (ch == '(') {
                ++paren_depth;
            } else if (ch == ')' && paren_depth > 0) {
                --paren_depth;
            } else if (ch == '{') {
                ++brace_depth;
            } else if (ch == '}' && brace_depth > 0) {
                --brace_depth;
            } else if (ch == '[') {
                ++bracket_depth;
            } else if (ch == ']' && bracket_depth > 0) {
                --bracket_depth;
            }
        }
        if ((at_end || ch == ',') && paren_depth == 0 && brace_depth == 0 &&
            bracket_depth == 0) {
            const std::string_view part =
                TrimWhitespace(body.substr(start, index - start));
            if (!part.empty()) {
                parts.push_back(part);
            }
            start = index + 1;
        }
    }

    if (parts.empty()) {
        return false;
    }

    for (std::string_view part : parts) {
        in_string = false;
        quote = '\0';
        paren_depth = 0;
        brace_depth = 0;
        bracket_depth = 0;
        std::size_t equals_pos = std::string_view::npos;
        for (std::size_t index = 0; index < part.size(); ++index) {
            const char ch = part[index];
            if (in_string) {
                if (ch == quote && (index == 0 || part[index - 1] != '\\')) {
                    in_string = false;
                }
                continue;
            }
            if (ch == '"' || ch == '\'') {
                in_string = true;
                quote = ch;
                continue;
            }
            if (ch == '(') {
                ++paren_depth;
            } else if (ch == ')' && paren_depth > 0) {
                --paren_depth;
            } else if (ch == '{') {
                ++brace_depth;
            } else if (ch == '}' && brace_depth > 0) {
                --brace_depth;
            } else if (ch == '[') {
                ++bracket_depth;
            } else if (ch == ']' && bracket_depth > 0) {
                --bracket_depth;
            } else if (ch == '=' && paren_depth == 0 && brace_depth == 0 &&
                       bracket_depth == 0) {
                equals_pos = index;
                break;
            }
        }
        if (equals_pos == std::string_view::npos || equals_pos == 0 ||
            equals_pos + 1 >= part.size()) {
            return false;
        }
        const std::string key =
            std::string(TrimWhitespace(part.substr(0, equals_pos)));
        const std::string value =
            std::string(TrimWhitespace(part.substr(equals_pos + 1)));
        if (!refii::lua::IsValidLuaIdentifier(key) || value.empty()) {
            return false;
        }
        fields_out->push_back(BytecodeRepairParsedTableField{key, value});
    }
    return !fields_out->empty();
}

}  // namespace refii::lua::internal
